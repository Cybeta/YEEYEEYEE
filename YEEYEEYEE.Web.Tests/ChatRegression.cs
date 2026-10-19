using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Web;

internal static class ChatRegression
{
    public static async Task Run(string dll, string root, bool settingsOnly = false, bool protocolOnly = false)
    {
        static int Port() { using var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
        static void Assert(bool value, string label) { if (!value) throw new Exception("Chat regression: " + label); }
        var directory = Path.Combine(root, "chat"); Directory.CreateDirectory(directory);
        var modelPort = Port(); var webPort = Port(); var mode = "success";
        var modelText = "回答";
        var imageFailureStatus = 0;
        TaskCompletionSource? imageGate = null;
        var project = Path.Combine(directory, "project");
        Directory.CreateDirectory(Path.Combine(project, "canvases"));
        Directory.CreateDirectory(Path.Combine(project, "project"));
        File.WriteAllText(Path.Combine(project, "project.json"), "{\"Name\":\"Chat test\"}");
        var entitiesPath = Path.Combine(project, "project", "entities.json");
        File.WriteAllText(entitiesPath, "{\"formatVersion\":1,\"entities\":[]}");
        var canvasPath = Path.Combine(project, "canvases", "main.json");
        var nodeId = Guid.NewGuid();
        File.WriteAllText(canvasPath, JsonSerializer.Serialize(new { Title = "Chat canvas", Revision = 1, FormatVersion = 0,
            Canvas = new { Nodes = new[] { new { Id = nodeId, Title = "Context node", Content = "Selected context body", Category = 0 } },
                Edges = Array.Empty<object>(), Entities = Array.Empty<object>(), WorkTree = Array.Empty<object>() } }));
        var captured = new List<JsonElement>();
        var referenceCounts = new List<int>();
        using var model = new HttpListener(); model.Prefixes.Add($"http://127.0.0.1:{modelPort}/"); model.Start();
        var modelLoop = Task.Run(async () =>
        {
            while (model.IsListening)
            {
                HttpListenerContext context;
                try { context = await model.GetContextAsync(); } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { break; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (context.Request.HttpMethod == "GET" && context.Request.Url!.AbsolutePath == "/about")
                        {
                            context.Response.ContentType = "application/json";
                            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"openapi\":\"3.0.0\",\"paths\":{\"/v1/videos\":{\"post\":{\"summary\":\"Generate video\"}}}}"));
                            context.Response.Close(); return;
                        }
                        if (context.Request.HttpMethod == "GET" && context.Request.Url!.AbsolutePath == "/admin/api/managed-models")
                        {
                            context.Response.ContentType = "application/json";
                            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"data\":[{\"type\":\"video\",\"enabled\":true,\"alias\":\"catalog(alias)\",\"resolutions\":[\"720p\"],\"durations\":[\"4s\"],\"prices\":{\"720p\":30},\"duration_prices\":{\"per_second\":24}}]}"));
                            context.Response.Close(); return;
                        }
                        using var reader = new StreamReader(context.Request.InputStream);
                        var bodyText = await reader.ReadToEndAsync();
                        if (context.Request.Url!.AbsolutePath.EndsWith("/images/edits"))
                        {
                            lock (referenceCounts) referenceCounts.Add(bodyText.Split("name=\"image[]\"", StringSplitOptions.None).Length - 1);
                            context.Response.ContentType = "application/json";
                            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"data\":[{\"b64_json\":\"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=\"}]}"));
                            context.Response.Close(); return;
                        }
                        var payload = JsonDocument.Parse(bodyText).RootElement.Clone();
                        lock (captured) captured.Add(payload);
                        if (context.Request.Url!.AbsolutePath.EndsWith("/images/generations"))
                        {
                            if (imageGate is { } pendingImage) await pendingImage.Task;
                            if (imageFailureStatus != 0) { context.Response.StatusCode = imageFailureStatus; context.Response.Close(); return; }
                            context.Response.ContentType = "application/json";
                            // 产物给一张**真能过格式校验**的 1×1 PNG（与 /images/edits 那张同一份）：
                            // 8 字节签名 + IHDR + 数据。假 PNG（只有签名）取字节时会被 ValidMedia 拒掉，
                            // 那样测不到「待审批产物能不能安全读出来」这条链路。
                            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"data\":[{\"b64_json\":\"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=\"}]}"));
                            context.Response.Close(); return;
                        }
                        var requestMode = mode;
                        var responseText = modelText;
                        if (requestMode == "protocol-example")
                        {
                            var protocol = payload.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content"))
                                .Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)
                                .Single(v => v.Contains("Web技能协议："));
                            var start = protocol.IndexOf("完整示例：", StringComparison.Ordinal) + "完整示例：".Length;
                            responseText = "```json\n" + protocol[start..protocol.IndexOf('。', start)] + "\n```";
                        }
                        if (requestMode == "fail") { context.Response.StatusCode = 500; context.Response.Close(); return; }
                        if (!payload.TryGetProperty("stream", out var stream) || !stream.GetBoolean())
                        {
                            context.Response.ContentType = "application/json";
                            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "OK" } } } })));
                            context.Response.Close(); return;
                        }
                        context.Response.ContentType = "text/event-stream"; context.Response.SendChunked = true;
                        async Task Frame(object value)
                        {
                            var bytes = Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(value) + "\n\n");
                            await context.Response.OutputStream.WriteAsync(bytes); await context.Response.OutputStream.FlushAsync();
                        }
                        if (payload.TryGetProperty("model", out var m) && m.GetString() == "anthropic-test")
                        {
                            await Frame(new { type = "content_block_delta", delta = new { type = "thinking_delta", thinking = "思考" } });
                            await Frame(new { type = "content_block_delta", delta = new { type = "text_delta", text = "回答" } });
                            await Frame(new { type = "message_delta", usage = new { input_tokens = 7, output_tokens = 3 } });
                        }
                        else
                        {
                            await Frame(new { choices = new[] { new { delta = new { reasoning_content = "思考", content = responseText } } } });
                            if (requestMode == "slow") await Task.Delay(2500);
                            await Frame(new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 7, completion_tokens = 3, prompt_cache_hit_tokens = 2 } });
                        }
                        context.Response.Close();
                    }
                    catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException) { }
                });
            }
        });
        var configPath = Path.Combine(directory, "web-ai-config.json");
        var desktopConfigPath = Path.Combine(directory, "desktop-ai.json");
        const string desktopConfig = "{\"Endpoint\":\"http://127.0.0.1:1\",\"Model\":\"desktop-only\",\"ApiKey\":\"desktop-test-key\"}";
        File.WriteAllText(desktopConfigPath, desktopConfig);
        void Config(bool anthropic = false, bool local = false) => File.WriteAllText(configPath, JsonSerializer.Serialize(new {
            UseLocalProvider = local, SelectedProfileId = "selected", Profiles = new[] {
                new { Id = "disabled", Enabled = false, Endpoint = "http://127.0.0.1:1", Model = "wrong", ApiKey = "plain:test" , ApiFormat = 0, SupportsImageInput = false },
                new { Id = "selected", Enabled = true, Endpoint = $"http://127.0.0.1:{modelPort}/v1", Model = anthropic ? "anthropic-test" : "openai-test", ApiKey = "plain:test", ApiFormat = anthropic ? 1 : 0, SupportsImageInput = true }
            }
        }));
        Config(); Process? server = null;
        Uri? attachmentBackend = null;
        async Task Start()
        {
            var start = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls http://127.0.0.1:{webPort}") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            start.Environment["YEEYEEYEE_CONFIG"] = desktopConfigPath;
            start.Environment["YEEYEEYEE__WebAiConfigPath"] = configPath;
            start.Environment["YEEYEEYEE__AssetDirectory"] = Path.Combine(project, "assets");
            start.Environment["YEEYEEYEE__ProjectCanvasPath"] = canvasPath;
            start.Environment["YEEYEEYEE__ProjectEntitiesPath"] = entitiesPath;
            start.Environment["YEEYEEYEE__WebSkillApprovalMode"] = "preapproved-local-image";
            start.Environment["ComfyUI__Checkpoint"] = "offline-test.safetensors";
            start.Environment["YEEYEEYEE__UserDatabasePath"] = Path.Combine(directory, "users.db");
            start.Environment["YEEYEEYEE__JobDatabasePath"] = Path.Combine(directory, "jobs.db");
            start.Environment["YEEYEEYEE__ChatDatabasePath"] = Path.Combine(directory, "chat.db");
            start.Environment["YEEYEEYEE__SetupToken"] = "setup-test";
            start.Environment["ComfyUI__BaseUrl"] = attachmentBackend?.ToString() ?? "http://127.0.0.1:1";
            if (attachmentBackend is not null)
            {
                start.Environment["YEEYEEYEE__WebVideoApprovalMode"] = "preapproved-local-video";
                start.Environment["WebVideoBackend"] = "ComfyUI";
                start.Environment["ComfyUI__LtxCheckpoint"] = "ltx-test.safetensors";
                start.Environment["ComfyUI__PollingInterval"] = "00:00:00.050";
            }
            foreach (var name in new[] { "YEEYEEYEE_AI_ENDPOINT", "YEEYEEYEE_AI_MODEL", "YEEYEEYEE_AI_KEY", "NOVELCANVAS_AI_ENDPOINT", "NOVELCANVAS_AI_MODEL", "NOVELCANVAS_AI_KEY" }) start.Environment.Remove(name);
            server = Process.Start(start)!; server.BeginOutputReadLine(); server.BeginErrorReadLine();
            using var probe = new HttpClient();
            for (var i = 0; i < 100; i++) { try { if ((await probe.GetAsync($"http://127.0.0.1:{webPort}/health")).IsSuccessStatusCode) return; } catch (HttpRequestException) { } await Task.Delay(100); }
            throw new Exception("Chat server startup timeout");
        }
        void Stop() { if (server is null) return; if (!server.HasExited) { server.Kill(true); server.WaitForExit(); } server.Dispose(); server = null; }
        using var admin = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://127.0.0.1:{webPort}") };
        using var other = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = admin.BaseAddress };
        async Task<JsonElement> Json(HttpClient client, string path, string? body = null, int status = 200, string method = "POST")
        {
            using var req = new HttpRequestMessage(body is null && method == "POST" ? HttpMethod.Get : new HttpMethod(method), path);
            if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(req); var text = await response.Content.ReadAsStringAsync();
            Assert((int)response.StatusCode == status, path + " status " + response.StatusCode + " " + text);
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        async Task<JsonElement> Send(string id, string body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/web/chat/sessions/{id}/messages") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var response = await admin.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            Assert(response.IsSuccessStatusCode, $"POST {req.RequestUri}: stream status {(int)response.StatusCode} ({response.StatusCode}), expected success");
            var text = await response.Content.ReadAsStringAsync(); Assert(text.Contains("event: done"), "terminal frame");
            var frames = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            var snapshot = JsonDocument.Parse(frames[0].Split("\ndata: ")[1]).RootElement;
            Assert(snapshot.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString() == "", "initial snapshot must not include later deltas");
            if (mode == "success") Assert(text.Contains("event: text") && text.Contains("event: thinking") && text.Contains("event: usage"), "independent SSE events");
            return await Json(admin, "/api/web/chat/sessions/" + id);
        }
        try
        {
            await Start(); admin.DefaultRequestHeaders.Add("X-Setup-Token", "setup-test");
            await Json(admin, "/api/auth/setup", "{\"username\":\"chatadmin\",\"password\":\"Password123!\"}");
            await Json(admin, "/api/auth/users", "{\"username\":\"chatother\",\"password\":\"Password123!\",\"role\":\"Viewer\"}");
            await Json(other, "/api/auth/login", "{\"username\":\"chatother\",\"password\":\"Password123!\"}");
            using var anonymous = new HttpClient { BaseAddress = admin.BaseAddress };
            await Json(anonymous, "/api/web/models/presets?canvas=missing-canvas", status: 401);
            var presets = await Json(other, "/api/web/models/presets?canvas=missing-canvas");
            Assert(presets.GetProperty("presets").GetArrayLength() > 0, "preset catalog available to authenticated viewer without canvas");
            await Json(admin, "/api/web/models?canvas=missing-canvas");
            await Json(other, "/api/web/settings?canvas=missing-canvas");
            await Json(other, "/api/web/settings/media?canvas=missing-canvas", status: 403);
            await Json(other, "/api/web/models/selected?canvas=missing-canvas", "{}", 403, "PUT");
            await Json(admin, "/api/web/scene?canvas=missing-canvas", status: 404);
            async Task ProtocolRegression()
            {
                int RequestCount() { lock (captured) return captured.Count; }
                foreach (var output in new[] {
                    "正在调用web.text-to-image。\n```json\n```",
                    "正在调用web.text-to-image。\r\n```JSON\r\n \t\r\n``` \r\n",
                    "```json\n{ \r\n }\n```",
                    "正在调用web.text-to-image。\n```json\n   ",
                    "```json\n{\"actions\":[]}\n```",
                    "```json\n{\"ask\":{} }\n```",
                    "```json\n{\"actions\":[" })
                {
                    modelText = output;
                    var sid = (await Json(admin, "/api/web/chat/sessions", "{}")).GetProperty("id").GetString()!;
                    var count = RequestCount();
                    var failed = await Send(sid, "{\"text\":\"生成图片\",\"mode\":\"readonly\"}");
                    var reply = failed.GetProperty("messages")[1];
                    Assert(reply.GetProperty("state").GetString() == "failed" &&
                        reply.GetProperty("error").GetString()!.Contains("动作协议为空或无效，未发起任务，请重试"), "empty/broken protocol explicitly failed");
                    Assert(reply.GetProperty("actions").GetArrayLength() == 0 && reply.GetProperty("skillActions").GetArrayLength() == 0 &&
                        reply.GetProperty("skillJobIds").GetArrayLength() == 0 && failed.GetProperty("jobIds").GetArrayLength() == 0,
                        "failed protocol must not invent actions or create jobs");
                    Assert(RequestCount() == count + 1, "failed protocol does not automatically retry provider");
                }
                foreach (var output in new[] { "你好", "正在调用web.text-to-image。", "示例：\n```json\n{\"prompt\":\"example\"}\n```",
                    "示例：\n```json\n[]\n```", "```json\n```\n以上仅作示例。" })
                {
                    modelText = output;
                    var sid = (await Json(admin, "/api/web/chat/sessions", "{}")).GetProperty("id").GetString()!;
                    var done = await Send(sid, "{\"text\":\"聊天\",\"mode\":\"readonly\"}");
                    Assert(done.GetProperty("messages")[1].GetProperty("state").GetString() == "completed" &&
                        done.GetProperty("messages")[1].GetProperty("content").GetString() == output &&
                        done.GetProperty("jobIds").GetArrayLength() == 0, "ordinary chat/nonempty JSON remains unchanged without invented actions");
                }
                modelText = "```json\n{\"ask\":{\"question\":\"需要什么风格？\"}}\n```";
                var askSession = (await Json(admin, "/api/web/chat/sessions", "{}")).GetProperty("id").GetString()!;
                var asked = await Send(askSession, "{\"text\":\"生成图片\",\"mode\":\"readonly\"}");
                Assert(asked.GetProperty("messages")[1].GetProperty("state").GetString() == "completed" &&
                    asked.GetProperty("messages")[1].GetProperty("ask").GetProperty("question").GetString() == "需要什么风格？", "valid ask remains available");
                var imageSettings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))!;
                imageSettings["ImageEndpoint"] = $"http://127.0.0.1:{modelPort}/v1";
                imageSettings["ImageModel"] = "offline-image";
                File.WriteAllText(configPath, imageSettings.ToJsonString());
                modelText = "正在调用web.text-to-image。\n```json\n```";
                var retrySession = (await Json(admin, "/api/web/chat/sessions", "{}")).GetProperty("id").GetString()!;
                await Send(retrySession, "{\"text\":\"生成图片\",\"mode\":\"readonly\"}");
                mode = "protocol-example";
                var retried = await Send(retrySession, "{\"retry\":true,\"mode\":\"readonly\"}");
                mode = "success";
                var retriedReply = retried.GetProperty("messages")[1];
                Assert(retried.GetProperty("messages").GetArrayLength() == 2 && retriedReply.GetProperty("state").GetString() == "completed" &&
                    retriedReply.GetProperty("skillActions").GetArrayLength() == 1 &&
                    retriedReply.GetProperty("skillActions")[0].GetProperty("target").GetString() == "web.text-to-image" &&
                    retriedReply.GetProperty("skillState").GetString() == "preview" && retried.GetProperty("jobIds").GetArrayLength() == 0,
                    "retry preserves valid skill protocol without duplicating user or creating job before ratio");
                lock (captured) Assert(captured.Last().GetProperty("messages").EnumerateArray().Any(m =>
                    m.GetProperty("content").ValueKind == JsonValueKind.String && m.GetProperty("content").GetString()!.Contains("不得仅声明正在调用技能")),
                    "request contains stronger skill protocol guidance");
                var path = $"/api/web/chat/sessions/{retrySession}/skills/{retriedReply.GetProperty("id").GetString()}/invoke";
                var countBeforeImage = RequestCount();
                await Json(admin, path, "{\"aspectRatio\":\"1:1\"}");
                await Json(admin, path, "{\"aspectRatio\":\"1:1\"}", 409);
                await Json(admin, $"/api/web/chat/sessions/{retrySession}/messages", "{\"retry\":true,\"mode\":\"readonly\"}", 409);
                var submitted = await Json(admin, "/api/web/chat/sessions/" + retrySession);
                Assert(submitted.GetProperty("jobIds").GetArrayLength() == 1 &&
                    submitted.GetProperty("messages")[1].GetProperty("skillJobIds").GetArrayLength() == 1, "successful retry submits exactly one job");
                for (var i = 0; i < 100; i++)
                {
                    var job = await Json(admin, "/api/web/jobs/" + submitted.GetProperty("jobIds")[0].GetString());
                    if (job.GetProperty("state").GetString() == "Succeeded") break;
                    Assert(job.GetProperty("state").GetString() != "Failed", "offline image job succeeds");
                    await Task.Delay(50);
                }
                var completed = await Json(admin, "/api/web/jobs/" + submitted.GetProperty("jobIds")[0].GetString());
                Assert(completed.GetProperty("state").GetString() == "Succeeded" && RequestCount() == countBeforeImage + 1,
                    "retry and duplicate invoke produce exactly one image provider request");
                async Task<JsonElement> WaitSkill(string sid, string state)
                {
                    for (var i = 0; i < 100; i++)
                    {
                        var current = await Json(admin, "/api/web/chat/sessions/" + sid);
                        if (current.GetProperty("messages")[1].GetProperty("skillState").GetString() == state) return current;
                        await Task.Delay(50);
                    }
                    throw new Exception("Skill state timeout: " + state);
                }
                foreach (var failureStatus in new[] { 500, 502 })
                {
                    modelText = "```json\n{\"actions\":[{\"kind\":\"invoke_skill\",\"target\":\"web.text-to-image\",\"content\":\"explicit retry fixture\"}]}\n```";
                    var sid = (await Json(admin, "/api/web/chat/sessions", "{}")).GetProperty("id").GetString()!;
                    var proposed = await Send(sid, "{\"text\":\"生成图片\",\"mode\":\"readonly\"}");
                    var invokePath = $"/api/web/chat/sessions/{sid}/skills/{proposed.GetProperty("messages")[1].GetProperty("id").GetString()}/invoke";
                    imageFailureStatus = failureStatus;
                    var beforeFailure = RequestCount();
                    var firstJob = await Json(admin, invokePath, "{\"aspectRatio\":\"1:1\"}");
                    var failedCard = await WaitSkill(sid, "failed");
                    var diagnostic = failedCard.GetProperty("messages")[1].GetProperty("skillError").GetString()!;
                    Assert(diagnostic.Contains($"初始URI=http://127.0.0.1:{modelPort}/v1/images/generations")
                        && diagnostic.Contains("请求HTTP=1.1") && diagnostic.Contains("响应HTTP=1.1")
                        && diagnostic.Contains("耗时ms=") && diagnostic.Contains("Web显式代理=未配置（使用系统默认）")
                        && !diagnostic.Contains("explicit retry fixture"), "failed card persists sanitized connection diagnostics");
                    var firstKey = failedCard.GetProperty("messages")[1].GetProperty("skillIdempotencyKey").GetString();
                    await Task.Delay(150);
                    Assert(RequestCount() == beforeFailure + 1, "500/502 image failure must not automatically retry");
                    await Json(other, invokePath, "{\"aspectRatio\":\"1:1\"}", 404);
                    var replayedFailure = await Json(admin, "/api/web/skills/web.text-to-image/invoke",
                        JsonSerializer.Serialize(new { prompt = "explicit retry fixture", aspectRatio = "1:1", idempotencyKey = firstKey }));
                    Assert(replayedFailure.GetProperty("jobId").GetGuid() == firstJob.GetProperty("jobId").GetGuid() &&
                        RequestCount() == beforeFailure + 1, "public failed-job idempotency remains unchanged");
                    imageFailureStatus = 0;
                    imageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var restart = Json(admin, invokePath, "{\"aspectRatio\":\"9:16\"}");
                    try
                    {
                        for (var i = 0; i < 100 && RequestCount() == beforeFailure + 1; i++) await Task.Delay(20);
                        Assert(RequestCount() == beforeFailure + 2, "explicit failed-card click reaches provider exactly once");
                        await Json(admin, invokePath, "{\"aspectRatio\":\"9:16\"}", 409);
                        await Task.Delay(1200);
                        var pending = await Json(admin, "/api/web/chat/sessions/" + sid);
                        // 发起接口现在提交后立即返回，卡片会从 preview 落到 submitted；
                        // 这里要盯的是「旧失败任务没有把这一条新尝试改回 failed」。
                        Assert(pending.GetProperty("messages")[1].GetProperty("skillState").GetString() == "submitted", "old failed job cannot overwrite pending new attempt");
                    }
                    finally { imageGate.SetResult(); imageGate = null; }
                    var secondJob = await restart;
                    var succeededCard = await WaitSkill(sid, "completed");
                    Assert(firstJob.GetProperty("jobId").GetGuid() != secondJob.GetProperty("jobId").GetGuid() &&
                        succeededCard.GetProperty("jobIds").GetArrayLength() == 2 &&
                        succeededCard.GetProperty("messages")[1].GetProperty("skillJobIds").GetArrayLength() == 2 &&
                        succeededCard.GetProperty("messages")[1].GetProperty("skillIdempotencyKey").GetString() != firstKey,
                        "new attempt has a persisted new key and preserves both job records");
                    await Json(admin, invokePath, "{\"aspectRatio\":\"9:16\"}", 409);
                    Assert(RequestCount() == beforeFailure + 2, "pending/successful duplicate clicks create no extra request");
                    Assert((await Json(admin, "/api/web/jobs/" + firstJob.GetProperty("jobId").GetString())).GetProperty("state").GetString() == "Failed", "old failure remains available");
                }
                modelText = "```json\n{\"actions\":[{\"kind\":\"invoke_skill\",\"target\":\"web.text-to-image\",\"content\":\"restore original image fixture\"}]}\n```";
                var restoreSession = (await Json(admin, "/api/web/chat/sessions", "{}")).GetProperty("id").GetString()!;
                var restoreProposal = await Send(restoreSession, JsonSerializer.Serialize(new { text = "生成图片", mode = "approval", nodeId }));
                var restoreMessageId = restoreProposal.GetProperty("messages")[1].GetProperty("id").GetString();
                var restoreActionPath = $"/api/web/chat/sessions/{restoreSession}/actions/{restoreMessageId}/";
                var originalJob = await Json(admin, $"/api/web/chat/sessions/{restoreSession}/skills/{restoreMessageId}/invoke", "{\"aspectRatio\":\"1:1\"}");
                var originalCard = (await WaitSkill(restoreSession, "completed")).GetProperty("messages")[1];
                var originalActions = originalCard.GetProperty("actions").GetRawText();
                var originalJobIds = originalCard.GetProperty("skillJobIds").GetRawText();
                using var fixtureJobs = new YEEYEEYEE.Host.SqliteJobStore(Path.Combine(directory, "jobs.db"));
                string OriginalInputs() => JsonSerializer.Serialize(fixtureJobs.LoadAll().Single(j => j.JobId == originalJob.GetProperty("jobId").GetGuid()).Inputs);
                var originalInputs = OriginalInputs();
                // 生成任务完成时，服务端往**同一条回复**追加了 attach_asset：消息 ID 与画布修订都没变，
                // 但动作集合变了——没重新预检就不许提交，旧的批准资格随动作变化失效。
                var staleApproval = await Json(admin, restoreActionPath + "commit", "{}", 409);
                Assert(staleApproval.GetProperty("code").GetString() == "ACTION_PREVIEW_REQUIRED", "appended action invalidates the previous approval");
                var rearmed = await Json(admin, restoreActionPath + "preview", "{}");
                Assert(rearmed.GetProperty("errors").GetArrayLength() == 0
                    && rearmed.GetProperty("revision").GetInt64() == originalCard.GetProperty("baseRevision").GetInt64(), "re-preview after append is clean at the same revision");
                await Json(admin, restoreActionPath + "commit", "{}");
                await Json(admin, restoreActionPath + "undo", "{}");
                var undoneCard = (await Json(admin, "/api/web/chat/sessions/" + restoreSession)).GetProperty("messages")[1];
                Assert(undoneCard.GetProperty("actionState").GetString() == "undone" && undoneCard.GetProperty("committedActionCount").GetInt32() == 0, "attachment undo leaves original proposal available");
                var beforeRestore = File.ReadAllBytes(canvasPath);
                var countBeforeRestore = RequestCount();
                await Json(other, restoreActionPath + "preview", "{}", 404);
                var restoredPreview = await Json(admin, restoreActionPath + "preview", "{}");
                var restoredCard = (await Json(admin, "/api/web/chat/sessions/" + restoreSession)).GetProperty("messages")[1];
                Assert(restoredCard.GetProperty("actionState").GetString() == "preview" && restoredCard.GetProperty("baseRevision").GetInt64() == restoredPreview.GetProperty("revision").GetInt64(), "undone attachment explicitly restores approval at current revision");
                Assert(restoredCard.GetProperty("actions").GetRawText() == originalActions && restoredCard.GetProperty("skillJobIds").GetRawText() == originalJobIds && restoredCard.GetProperty("mode").GetString() == "approval", "restore preserves original actions, jobs and mode");
                Assert(beforeRestore.SequenceEqual(File.ReadAllBytes(canvasPath)) && RequestCount() == countBeforeRestore, "restore previews without canvas writes or provider calls");
                // 待提交的产物还没进正式节点，正式那条按节点取产物的接口读不到它；预览媒体接口按这批动作
                // 重新试算后再取，并且只服务「属于本会话、且还停在待审批预览」的那条消息。
                var previewProjection = restoredPreview.GetProperty("diff").GetProperty("updatedNodes")[0];
                var previewRecordId = previewProjection.GetProperty("recordId").GetString();
                var previewAttachment = previewProjection.GetProperty("record").GetProperty("attachments")[0];
                var previewAttachmentId = previewAttachment.GetProperty("id").GetString();
                var previewMediaPath = $"/api/web/chat/sessions/{restoreSession}/messages/{restoreMessageId}"
                    + $"/preview/records/{previewRecordId}/attachments/{previewAttachmentId}/file";
                Assert(previewAttachment.GetProperty("kind").GetString() == "Image", "pending product is projected with its media kind");
                string Describe(HttpResponseMessage response, byte[] bytes) =>
                    $" [{(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType} {Encoding.UTF8.GetString(bytes, 0, Math.Min(120, bytes.Length))}]";
                using (var pendingMedia = await admin.GetAsync(previewMediaPath))
                {
                    var pendingBytes = await pendingMedia.Content.ReadAsByteArrayAsync();
                    Assert(pendingMedia.IsSuccessStatusCode && pendingBytes.Length > 0,
                        "pending product bytes come from the preview trial" + Describe(pendingMedia, pendingBytes));
                }
                using (var foreignMedia = await other.GetAsync(previewMediaPath))
                    Assert(foreignMedia.StatusCode == HttpStatusCode.NotFound, "another account cannot read pending preview media");
                using (var unrelated = await admin.GetAsync(previewMediaPath.Replace(previewAttachmentId!, Guid.NewGuid().ToString())))
                    Assert(unrelated.StatusCode == HttpStatusCode.NotFound, "products outside this batch are not served by the preview media endpoint");
                Assert(beforeRestore.SequenceEqual(File.ReadAllBytes(canvasPath)) && RequestCount() == countBeforeRestore, "reading preview media writes nothing and calls no provider");
                await Json(admin, restoreActionPath + "commit", "{}");
                using (var staleMedia = await admin.GetAsync(previewMediaPath))
                    Assert(staleMedia.StatusCode == HttpStatusCode.Conflict, "committed proposals are no longer served as pending preview media");
                using (var committedMedia = await admin.GetAsync($"/api/web/records/{previewRecordId}/attachments/{previewAttachmentId}/file"))
                    Assert(committedMedia.IsSuccessStatusCode, "the committed product is readable through the formal record media endpoint");
                var committedCanvas = JsonDocument.Parse(File.ReadAllBytes(canvasPath)).RootElement;
                Assert(committedCanvas.GetProperty("Canvas").GetProperty("Nodes")[0].GetProperty("Attachments").GetArrayLength() == 1, "restored original attachment commits once through approval");
                Assert(OriginalInputs() == originalInputs && RequestCount() == countBeforeRestore, "reapproval preserves Job inputs without rerunning skill");
                await Json(admin, restoreActionPath + "undo", "{}");

                var fixtureStore = new WebChatStore(Path.Combine(directory, "chat.db"));
                string fixtureOwner;
                using (var fixtureDb = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(directory, "chat.db")))
                {
                    fixtureDb.Open();
                    using var query = fixtureDb.CreateCommand();
                    query.CommandText = "SELECT owner FROM chat_sessions WHERE id=$id";
                    query.Parameters.AddWithValue("$id", restoreSession);
                    fixtureOwner = (string)query.ExecuteScalar()!;
                }
                var originalUndone = JsonSerializer.Serialize(fixtureStore.Get(fixtureOwner, restoreSession)!);
                foreach (var rejection in new[] { "readonly", "non-attachment", "missing-job" })
                {
                    var fixtureSession = JsonSerializer.Deserialize<WebChatSession>(originalUndone)!;
                    var fixtureMessage = fixtureSession.Messages[1];
                    if (rejection == "readonly") fixtureMessage.Mode = "readonly";
                    else if (rejection == "non-attachment") fixtureMessage.Actions[0].Kind = "update_node";
                    else fixtureMessage.SkillJobIds.Clear();
                    fixtureStore.Save(fixtureOwner, fixtureSession);
                    var unchangedCanvas = File.ReadAllBytes(canvasPath);
                    var rejected = await Json(admin, restoreActionPath + "preview", "{}", rejection == "readonly" ? 403 : 409);
                    Assert(rejected.GetProperty("code").GetString() == (rejection == "readonly" ? "ACTION_READ_ONLY" : rejection == "non-attachment" ? "ACTION_STATE_INVALID" : "ACTION_PRECHECK_FAILED"), "restore rejects " + rejection);
                    var rejectedMessage = fixtureStore.Get(fixtureOwner, restoreSession)!.Messages[1];
                    Assert(rejectedMessage.ActionState == "undone" && rejectedMessage.BaseRevision == fixtureMessage.BaseRevision && unchangedCanvas.SequenceEqual(File.ReadAllBytes(canvasPath)), "rejected restore preserves state and canvas: " + rejection);
                }
                fixtureStore.Save(fixtureOwner, JsonSerializer.Deserialize<WebChatSession>(originalUndone)!);
                Assert(RequestCount() == countBeforeRestore, "rejected restoration never invokes provider");

                // 临时性失败（产物暂时回挂不上）消失之后重新预检：预检的结论必须**覆盖**旧的
                // Hints / ActionErrors，否则界面会拿着上一次的错误一直挡着批准。
                var transient = JsonSerializer.Deserialize<WebChatSession>(originalUndone)!;
                transient.Messages[1].SkillJobIds.Clear();
                fixtureStore.Save(fixtureOwner, transient);
                var blocked = await Json(admin, restoreActionPath + "preview", "{}", 409);
                Assert(blocked.GetProperty("code").GetString() == "ACTION_PRECHECK_FAILED", "transient attachment failure blocks the preview");
                var blockedCard = fixtureStore.Get(fixtureOwner, restoreSession)!.Messages[1];
                Assert(blockedCard.ActionErrors.Length > 0 && blockedCard.PreviewFingerprint is null, "failed preview records the reason and arms nothing");
                fixtureStore.Save(fixtureOwner, JsonSerializer.Deserialize<WebChatSession>(originalUndone)!);
                var recoveredPreview = await Json(admin, restoreActionPath + "preview", "{}");
                Assert(recoveredPreview.GetProperty("errors").GetArrayLength() == 0, "recovered preview reports no errors");
                var recoveredCard = fixtureStore.Get(fixtureOwner, restoreSession)!.Messages[1];
                Assert(recoveredCard.ActionErrors.Length == 0 && recoveredCard.PreviewFingerprint is not null, "recovered preview clears stale errors and arms approval");

                // 预检**没能跑完**（画布文件读不出来）与「动作校验没过」是两件事：
                // 前者不往 ActionErrors 里塞东西（那是动作校验的字段），但上一次预检留下的批准资格必须收掉——
                // 这批动作这次没有被重新核对过。修好之后重新预检应当照常通过。
                var goodCanvas = File.ReadAllBytes(canvasPath);
                File.WriteAllText(canvasPath, "{ 这不是一份合法画布 ");
                var unfinished = await Json(admin, restoreActionPath + "preview", "{}", 409);
                Assert(unfinished.GetProperty("code").GetString() == "ACTION_PREVIEW_FAILED",
                    "unfinished preview is reported as an execution failure, not a validation failure");
                var unfinishedCard = fixtureStore.Get(fixtureOwner, restoreSession)!.Messages[1];
                Assert(unfinishedCard.ActionErrors.Length == 0 && unfinishedCard.PreviewFingerprint is null,
                    "unfinished preview does not fake a validation error and drops the stale approval");
                File.WriteAllBytes(canvasPath, goodCanvas);
                var recoveredAfterFailure = await Json(admin, restoreActionPath + "preview", "{}");
                Assert(recoveredAfterFailure.GetProperty("errors").GetArrayLength() == 0, "preview recovers once the canvas is readable again");
                Assert(fixtureStore.Get(fixtureOwner, restoreSession)!.Messages[1].PreviewFingerprint is not null,
                    "recovered preview arms approval again");

                // 预检之后动作又变了：服务端按指纹拒绝旧批准——不能只靠前端把按钮灰掉。
                var tampered = JsonSerializer.Deserialize<WebChatSession>(originalUndone)!;
                tampered.Messages[1].ActionState = "preview";
                tampered.Messages[1].BaseRevision = recoveredCard.BaseRevision;
                tampered.Messages[1].PreviewFingerprint = recoveredCard.PreviewFingerprint;
                tampered.Messages[1].Actions[0].Title = "预检之后被改过的标题";
                fixtureStore.Save(fixtureOwner, tampered);
                var unchangedBytes = File.ReadAllBytes(canvasPath);
                var staleCommit = await Json(admin, restoreActionPath + "commit", "{}", 409);
                Assert(staleCommit.GetProperty("code").GetString() == "ACTION_PREVIEW_REQUIRED"
                    && unchangedBytes.SequenceEqual(File.ReadAllBytes(canvasPath)), "changed actions after preview are rejected server-side without writing");

                // 按最新动作重新预检之后才放行。
                fixtureStore.Save(fixtureOwner, JsonSerializer.Deserialize<WebChatSession>(originalUndone)!);
                await Json(admin, restoreActionPath + "preview", "{}");
                await Json(admin, restoreActionPath + "commit", "{}");
                Assert(JsonDocument.Parse(File.ReadAllBytes(canvasPath)).RootElement.GetProperty("Canvas").GetProperty("Nodes")[0]
                    .GetProperty("Attachments").GetArrayLength() == 1, "re-preview after an action change restores approval and commits once");
                await Json(admin, restoreActionPath + "undo", "{}");
                Config(); modelText = "回答";
                Console.WriteLine("Chat protocol regression passed: empty/broken protocol failure, ordinary chat/JSON, valid ask/skill, explicit retry, single Job and undone attachment reapproval (local fixture)");
            }
            if (!settingsOnly) await ProtocolRegression();
            if (protocolOnly) return;
            lock (captured) captured.Clear();
            var id = "";
            if (!settingsOnly)
            {
            var s = await Json(admin, "/api/web/chat/sessions", "{}"); id = s.GetProperty("id").GetString()!;
            Assert((await Json(other, "/api/web/chat/sessions")).GetArrayLength() == 0, "owner list isolation");
            await Json(other, "/api/web/chat/sessions/" + id, status: 404);
            await Json(other, "/api/web/chat/sessions/" + id, "{}", 404, "DELETE");
            await Json(other, $"/api/web/chat/sessions/{id}/cancel", "{}", 404);
            var viewerSession = await Json(other, "/api/web/chat/sessions", "{}");
            await Json(other, $"/api/web/chat/sessions/{viewerSession.GetProperty("id").GetString()}/messages", "{\"text\":\"hello\"}", 403);
            var configBefore = File.ReadAllText(configPath);
            var first = await Send(id, "{\"text\":\"你好\"}");
            Assert(File.ReadAllText(configPath) == configBefore, "chat configuration read must not rewrite desktop settings");
            var reply = first.GetProperty("messages")[1];
            Assert(reply.GetProperty("content").GetString() == "回答" && reply.GetProperty("thinking").GetString() == "思考", "separate streams: " + reply);
            Assert(reply.GetProperty("usage").GetProperty("inputTokens").GetInt64() == 7, "reported usage");
            Assert(captured[0].GetProperty("model").GetString() == "openai-test" && captured[0].GetProperty("stream").GetBoolean(), "selected desktop profile");
            await Send(id, "{\"text\":\"继续\"}");
            Assert(!captured[1].ToString().Contains("思考"), "thinking excluded from history");
            mode = "fail"; var failed = await Send(id, JsonSerializer.Serialize(new { text = "失败测试", nodeId, mode = "readonly", source = "node-menu", targetNodeTitle = "Original target" }));
            Assert(failed.GetProperty("messages")[5].GetProperty("state").GetString() == "failed", "failure persisted");
            mode = "success"; var retried = await Send(id, JsonSerializer.Serialize(new { retry = true, mode = "auto", nodeId = Guid.NewGuid(), chapterId = Guid.NewGuid(), source = "changed", targetNodeTitle = "Changed target" }));
            foreach (var field in new[] { "id", "targetNodeId", "targetChapterId", "baseRevision", "mode", "source", "targetNodeTitle" })
                Assert(retried.GetProperty("messages")[5].GetProperty(field).ToString() == failed.GetProperty("messages")[5].GetProperty(field).ToString(), "retry preserves " + field);
            foreach (var field in new[] { "targetNodeId", "targetChapterId", "baseRevision", "mode", "source", "targetNodeTitle" })
                Assert(failed.GetProperty("messages")[4].GetProperty(field).ToString() == failed.GetProperty("messages")[5].GetProperty(field).ToString(), "user and assistant bind " + field);
            Assert(retried.GetProperty("messages").GetArrayLength() == 6 && retried.GetProperty("messages")[5].GetProperty("state").GetString() == "completed", "retry does not duplicate user");
            Config(anthropic: true); var anth = await Send(id, "{\"text\":\"另一协议\"}");
            Assert(anth.GetProperty("messages")[7].GetProperty("content").GetString() == "回答", "Anthropic provider"); Config();
            mode = "slow"; var slow = Send(id, "{\"text\":\"取消测试\"}");
            for (var i = 0; i < 100; i++) { var current = await Json(admin, "/api/web/chat/sessions/" + id); if (current.GetProperty("messages").EnumerateArray().Last().GetProperty("state").GetString() == "streaming") break; await Task.Delay(20); }
            await Json(admin, $"/api/web/chat/sessions/{id}/messages", "{\"text\":\"并行\"}", 409);
            await Json(admin, "/api/web/chat/sessions/" + id, "{}", 409, "DELETE");
            await Json(admin, $"/api/web/chat/sessions/{id}/cancel", "{}");
            var cancelled = await slow; Assert(cancelled.GetProperty("messages")[9].GetProperty("state").GetString() == "cancelled", "cancellation persisted");
            Stop(); await Start(); var restored = await Json(admin, "/api/web/chat/sessions/" + id);
            Assert(restored.GetProperty("messages").GetArrayLength() == 10, "restart restore");
            mode = "success";
            var resumed = await Send(id, "{\"retry\":true}");
            Assert(resumed.GetProperty("messages").GetArrayLength() == 10 && resumed.GetProperty("messages")[9].GetProperty("state").GetString() == "completed", "cancelled reply retry after restart");
            }
            // 模型增删改查不能回显凭证，切换模型只需修订号。
            var models = await Json(admin, "/api/web/models");
            var revision = models.GetProperty("revision").GetString();
            Assert(!models.ToString().Contains("desktop-only"), "desktop profiles must not be imported");
            var summary = await Json(admin, "/api/web/settings");
            Assert(!summary.ToString().Contains("desktop-only"), "summary uses independent server configuration");
            string ModelBody(string? rev, string? secret = null) => JsonSerializer.Serialize(new { revision = rev, displayName = "", endpoint = $"http://127.0.0.1:{modelPort}/v1", model = "openai-test", apiKey = secret });
            await Json(other, "/api/web/models/web-test", ModelBody(revision), 403, "PUT");
            await Json(admin, "/api/web/models/web-test", ModelBody(revision, "new-private-key"), 200, "PUT");
            Assert(!File.ReadAllText(configPath).Contains("new-private-key"), "key encrypted at rest");
            models = await Json(admin, "/api/web/models");
            Assert(!models.ToString().Contains("new-private-key") && !models.ToString().Contains("ApiKey"), "public model projection excludes key");
            await Json(admin, "/api/web/models/web-test", ModelBody(revision), 409, "PUT");
            revision = models.GetProperty("revision").GetString();
            await Json(admin, "/api/web/models/web-test", ModelBody(revision), 200, "PUT");
            models = await Json(admin, "/api/web/models");
            await Json(admin, "/api/web/models/web-test/select", JsonSerializer.Serialize(new { revision = models.GetProperty("revision").GetString() }));
            Assert((await Json(admin, "/api/web/models")).GetProperty("selectedProfileId").GetString() == "web-test", "model selected");
            await Json(admin, "/api/web/models/web-test/test", "{}");
            mode = "fail"; await Json(admin, "/api/web/models/web-test/test", "{}", 502); mode = "success";
            models = await Json(admin, "/api/web/models");
            revision = models.GetProperty("revision").GetString();
            var mutation = JsonSerializer.Serialize(new { revision });
            await Json(other, "/api/web/models/web-test/clear-key", mutation, 403);
            await Json(admin, "/api/web/models/web-test/clear-key", "{\"revision\":\"stale\"}", 409);
            await Json(admin, "/api/web/models/web-test/clear-key", mutation);
            models = await Json(admin, "/api/web/models");
            Assert(!models.GetProperty("profiles").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "web-test").GetProperty("hasKey").GetBoolean(), "clear key removes profile credential");
            Assert(JsonDocument.Parse(File.ReadAllText(configPath)).RootElement.GetProperty("ApiKey").GetString() == "", "clear key removes active credential copy");
            revision = models.GetProperty("revision").GetString();
            await Json(other, "/api/web/models/web-test?revision=" + revision, status: 403, method: "DELETE");
            await Json(admin, "/api/web/models/web-test?revision=stale", status: 409, method: "DELETE");
            await Json(admin, "/api/web/models/web-test?revision=" + revision, method: "DELETE");
            models = await Json(admin, "/api/web/models");
            Assert(!models.GetProperty("profiles").EnumerateArray().Any(p => p.GetProperty("id").GetString() == "web-test"), "delete removes profile");
            Assert(models.GetProperty("selectedProfileId").GetString() != "web-test", "delete resets active selection");
            await Json(admin, "/api/web/models/web-test?revision=" + models.GetProperty("revision").GetString(), status: 404, method: "DELETE");
            var media = await Json(admin, "/api/web/settings/media");
            string MediaBody(string? rev, string? imageKey = null, string? videoKey = null, bool clear = false, string endpoint = "http://127.0.0.1:8188",
                string? imageProxy = null, string? videoProxy = null) =>
                JsonSerializer.Serialize(new { revision = rev, imageEndpoint = endpoint, imageModel = "image-test", comfyUiBaseUrl = "http://127.0.0.1:8188",
                    comfyUiCheckpoint = "test.safetensors", videoEndpoint = endpoint, videoModel = "video-test", imageApiKey = imageKey,
                    videoApiKey = videoKey, clearImageKey = clear, clearVideoKey = clear, imageProxyUrl = imageProxy, videoProxyUrl = videoProxy });
            await Json(other, "/api/web/settings/media", status: 403);
            revision = media.GetProperty("revision").GetString();
            await Json(other, "/api/web/settings/media", MediaBody(revision), 403, "PUT");
            await Json(admin, "/api/web/settings/media", MediaBody(revision, endpoint: "file:///tmp/media"), 400, "PUT");
            await Json(admin, "/api/web/settings/media", MediaBody("stale"), 409, "PUT");
            foreach (var proxy in new[] { "127.0.0.1:10809", "file:///tmp/proxy", "http://user:password@localhost:10809", "http://localhost:10809/path", "http://localhost:10809?token=secret", "http://localhost:10809#fragment" })
            {
                await Json(admin, "/api/web/settings/media", MediaBody(revision, imageProxy: proxy), 400, "PUT");
                await Json(admin, "/api/web/settings/media", MediaBody(revision, videoProxy: proxy), 400, "PUT");
            }
            await Json(admin, "/api/web/settings/media", MediaBody(revision, "image-private-secret", "video-private-secret",
                imageProxy: " http://127.0.0.1:10809 ", videoProxy: "socks5://127.0.0.1:10808"), method: "PUT");
            media = await Json(admin, "/api/web/settings/media");
            var mediaDisk = File.ReadAllText(configPath);
            Assert(!mediaDisk.Contains("image-private-secret") && !mediaDisk.Contains("video-private-secret"), "media keys encrypted at rest");
            Assert(!media.ToString().Contains("private-secret") && media.GetProperty("imageHasKey").GetBoolean() && media.GetProperty("videoHasKey").GetBoolean(), "media projection exposes presence only");
            Assert(JsonDocument.Parse(mediaDisk).RootElement.GetProperty("ImageModel").GetString() == "image-test", "media writes independent server configuration");
            Assert(media.GetProperty("imageProxyUrl").GetString() == "http://127.0.0.1:10809" &&
                media.GetProperty("videoProxyUrl").GetString() == "socks5://127.0.0.1:10808", "independent media proxy projection and trimming");
            var savedMedia = JsonDocument.Parse(mediaDisk).RootElement;
            Assert(savedMedia.GetProperty("ImageProxyUrl").GetString() == "http://127.0.0.1:10809" &&
                savedMedia.GetProperty("VideoProxyUrl").GetString() == "socks5://127.0.0.1:10808", "independent media proxy persistence");
            await Json(admin, "/api/web/settings/media", MediaBody(media.GetProperty("revision").GetString()), method: "PUT");
            media = await Json(admin, "/api/web/settings/media");
            Assert(media.GetProperty("imageHasKey").GetBoolean() && media.GetProperty("videoHasKey").GetBoolean(), "blank media keys preserve credentials");
            Assert(media.GetProperty("imageProxyUrl").GetString() == "http://127.0.0.1:10809" &&
                media.GetProperty("videoProxyUrl").GetString() == "socks5://127.0.0.1:10808", "omitted proxy settings preserve existing values");
            await Json(admin, "/api/web/settings/media", MediaBody(media.GetProperty("revision").GetString(), imageProxy: "", videoProxy: ""), method: "PUT");
            media = await Json(admin, "/api/web/settings/media");
            Assert(media.GetProperty("imageProxyUrl").GetString() == "" && media.GetProperty("videoProxyUrl").GetString() == "", "empty proxy settings restore default transport");
            var readVideoModels = System.Reflection.Assembly.Load("YEEYEEYEE.Web").GetType("YEEYEEYEE.Web.WebSettingsApi")!
                .GetMethod("ReadVideoModels", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var videoModels = JsonSerializer.SerializeToElement(readVideoModels.Invoke(null, new object[] { """
                {"data":[
                  {"type":"video","enabled":true,"alias":"full(alias)","resolutions":["720p","1080p"],"durations":["4s","8s"],"prices":{"720p":30,"1080p":90},"duration_prices":{"per_second":24},"prices_agent":{"720p":1}},
                  {"type":"video","enabled":true,"alias":"fixed","resolutions":["720p"],"durations":["15s"],"prices":{"720p":100},"duration_prices":{"15s":20}},
                  {"type":"video","enabled":false,"alias":"disabled"}]}
                """ }));
            Assert(videoModels.GetArrayLength() == 2 && videoModels[0].GetProperty("Alias").GetString() == "full(alias)" && videoModels[0].GetProperty("Price").GetDecimal() == 126 &&
                videoModels[0].GetProperty("Seconds").GetInt32() == 4 && videoModels[0].GetProperty("Width").GetInt32() == 1280 && videoModels[0].GetProperty("Height").GetInt32() == 720 && videoModels[1].GetProperty("Price").GetDecimal() == 120,
                "video catalog uses full alias, enabled models, minimum resolution plus duration price and per-second price without agent price");
            await Json(other, "/api/web/settings/media/video/inspect", "{\"url\":\"https://example.com/about\"}", 403, "POST");
            await Json(admin, "/api/web/settings/media/video/inspect", "{\"url\":\"file:///tmp/doc\"}", 400, "POST");
            var beforeInspect = media.GetProperty("revision").GetString();
            var inspectedVideo = await Json(admin, "/api/web/settings/media/video/inspect", JsonSerializer.Serialize(new { url = $"http://127.0.0.1:{modelPort}/about" }), method: "POST");
            Assert(inspectedVideo.GetProperty("endpoint").GetString() == $"http://127.0.0.1:{modelPort}/v1" &&
                inspectedVideo.GetProperty("models")[0].GetProperty("alias").GetString() == "catalog(alias)" &&
                inspectedVideo.GetProperty("models")[0].GetProperty("price").GetDecimal() == 126,
                "read-only video inspect resolves document endpoint and catalog parameters");
            Assert((await Json(admin, "/api/web/settings/media")).GetProperty("revision").GetString() == beforeInspect, "inspect never saves configuration");
            string ReuseBody(string videoEndpoint, string? rev = null) => JsonSerializer.Serialize(new {
                revision = rev ?? media.GetProperty("revision").GetString(), imageEndpoint = "http://127.0.0.1:8188", imageModel = "image-test",
                comfyUiBaseUrl = "", comfyUiCheckpoint = "", videoEndpoint, videoModel = "full(alias)", videoDefaultSeconds = 4, reuseImageKeyForVideo = true });
            await Json(admin, "/api/web/settings/media", ReuseBody("http://127.0.0.1:8189/v1"), 400, "PUT");
            await Json(admin, "/api/web/settings/media", ReuseBody("http://127.0.0.1:8188/v1", "stale"), 409, "PUT");
            await Json(admin, "/api/web/settings/media", ReuseBody("http://127.0.0.1:8188/v1"), method: "PUT");
            media = await Json(admin, "/api/web/settings/media");
            Assert(media.GetProperty("videoDefaultSeconds").GetInt32() == 4 && !media.ToString().Contains("private-secret"),
                "video default seconds persist and reused key never appears in projection");
            var reusedConfig = JsonDocument.Parse(File.ReadAllText(configPath)).RootElement;
            Assert(reusedConfig.GetProperty("ImageApiKey").GetString() == reusedConfig.GetProperty("VideoApiKey").GetString(), "reuse copies saved encrypted image key");
            await Json(admin, "/api/web/settings/media", MediaBody(media.GetProperty("revision").GetString(), clear: true), method: "PUT");
            media = await Json(admin, "/api/web/settings/media");
            Assert(!media.GetProperty("imageHasKey").GetBoolean() && !media.GetProperty("videoHasKey").GetBoolean(), "explicit media key clearing");
            Assert(File.ReadAllText(desktopConfigPath) == desktopConfig, "model and media settings must not modify desktop configuration");
            Console.WriteLine("Media settings regression passed: admin permission, URL validation, revision conflict, independent server persistence, encrypted keys, presence-only projection, key preservation and clearing");
            if (settingsOnly)
            {
                Console.WriteLine("Settings regression passed: preset catalog authentication, viewer access, canvas independence and preserved canvas validation");
                return;
            }
            Config(); // 后续 Agent 回归前恢复测试模型。
            Console.WriteLine("Model removal regression passed: admin permission, stale revision, profile and active key clearing, deletion, selection reset and missing profile");

            async Task<(string Session, string Message)> Proposal(string executionMode = "approval", string? output = null)
            {
                modelText = output ?? "拟议修改。\n```json\n" + JsonSerializer.Serialize(new { actions = new[] { new { kind = "update_node", target = nodeId.ToString(), title = "Agent changed", content = "Agent body" } } }) + "\n```";
                var created = await Json(admin, "/api/web/chat/sessions", "{}");
                var sid = created.GetProperty("id").GetString()!;
                var scene = await Json(admin, "/api/web/scene");
                var done = await Send(sid, JsonSerializer.Serialize(new { text = "修改选中节点", mode = executionMode, nodeId, baseRevision = scene.GetProperty("revision").GetInt64() }));
                var messageId = done.GetProperty("messages")[1].GetProperty("id").GetString()!;
                var reply = done.GetProperty("messages")[1];
                if (reply.TryGetProperty("skillActions", out var actions) && actions.GetArrayLength() == 1 &&
                    actions[0].GetProperty("target").GetString() is "comfyui.text-to-image" or "web.text-to-image" &&
                    (!reply.TryGetProperty("ask", out var askValue) || askValue.ValueKind == JsonValueKind.Null))
                {
                    Assert(done.GetProperty("jobIds").GetArrayLength() == 0, "image proposal waits for user ratio in every mode");
                    await Json(admin, $"/api/web/chat/sessions/{sid}/skills/{messageId}/invoke", "{}", 400);
                    var action = actions[0];
                    var unsupportedInput = action.TryGetProperty("unsupportedSkillFields", out var unsupported) && unsupported.GetArrayLength() > 0 ||
                        new[] { "seconds", "width", "height" }.Any(field => action.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null) ||
                        new[] { "source", "path" }.Any(field => action.TryGetProperty(field, out var value) && !string.IsNullOrEmpty(value.GetString())) ||
                        action.TryGetProperty("entityTargets", out var entityTargets) && entityTargets.GetArrayLength() > 0;
                    await Json(admin, $"/api/web/chat/sessions/{sid}/skills/{messageId}/invoke", "{\"aspectRatio\":\"16:9\"}", unsupportedInput ? 400 : 200);
                }
                return (sid, messageId);
            }
            // 技能任务的完成回调（CompleteJob）会短暂占用会话忙碌标记来落库 skillState、
            // 回挂产物并释放锁。只读提交如果在这段窗口里发出，会先得到 409 CHAT_BUSY，
            // 而不是权限拒绝。等技能状态落定后再断言 403；真正的并发生成仍由
            // “生成期间并行发送返回 409”的用例覆盖，不能放宽这里的权限断言。
            async Task<JsonElement> WaitSkillSettled((string Session, string Message) p)
            {
                for (var i = 0; i < 100; i++)
                {
                    var session = await Json(admin, "/api/web/chat/sessions/" + p.Session);
                    var message = session.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("id").GetString() == p.Message);
                    if (message.GetProperty("skillState").GetString() is "completed" or "failed") return message;
                    await Task.Delay(50);
                }
                throw new Exception("skill did not settle: " + p.Session);
            }
            string ActionPath((string Session, string Message) p, string op) => $"/api/web/chat/sessions/{p.Session}/actions/{p.Message}/{op}";
            var ordinaryScene = await Json(admin, "/api/web/scene");
            var ordinaryBefore = File.ReadAllBytes(canvasPath);
            await Json(admin, "/api/web/records/" + nodeId, JsonSerializer.Serialize(new { baseRevision = ordinaryScene.GetProperty("revision").GetInt64(), title = "History title", content = "History body" }), method: "PUT");
            var history = await Json(admin, "/api/web/canvas/history");
            Assert(history.GetProperty("undo").GetInt32() == 1, "ordinary edit captured");
            var ordinaryAfter = File.ReadAllBytes(canvasPath);
            await Json(other, "/api/web/canvas/history/undo", JsonSerializer.Serialize(new { baseRevision = history.GetProperty("revision").GetInt64() }), 403);
            await Json(admin, "/api/web/canvas/history/undo", "{\"baseRevision\":0}", 409);
            await Json(admin, "/api/web/canvas/history/undo", JsonSerializer.Serialize(new { baseRevision = history.GetProperty("revision").GetInt64() }));
            Assert(File.ReadAllText(canvasPath).Contains("Selected context body"), "ordinary undo restores desktop content");
            history = await Json(admin, "/api/web/canvas/history");
            Assert(history.GetProperty("redo").GetInt32() == 1, "undo enables redo");
            await Json(admin, "/api/web/canvas/history/redo", JsonSerializer.Serialize(new { baseRevision = history.GetProperty("revision").GetInt64() }));
            Assert(File.ReadAllText(canvasPath).Contains("History body"), "ordinary redo restores edit");
            history = await Json(admin, "/api/web/canvas/history");
            await Json(admin, "/api/web/canvas/history/undo", JsonSerializer.Serialize(new { baseRevision = history.GetProperty("revision").GetInt64() }));
            File.WriteAllBytes(canvasPath, ordinaryBefore);
            Console.WriteLine("Ordinary history regression passed: captured edit, permission, stale revision, undo, redo and actual desktop content restoration");
            var treeBefore = File.ReadAllBytes(canvasPath);
            var treePlan = await Json(admin, "/api/web/worktree/plan?direction=Both");
            Assert(treeBefore.SequenceEqual(File.ReadAllBytes(canvasPath)), "worktree preview is read only");
            await Json(admin, "/api/web/worktree/plan?direction=invalid", status: 400);
            var treeApply = JsonSerializer.Serialize(new { baseRevision = treePlan.GetProperty("revision").GetInt64(), direction = "Both", baselineHash = "stale", confirmed = true });
            await Json(other, "/api/web/worktree/apply", treeApply, 403);
            await Json(admin, "/api/web/worktree/apply", treeApply, 409);
            Assert(treeBefore.SequenceEqual(File.ReadAllBytes(canvasPath)), "stale sync leaves bytes unchanged");
            var splitScene = await Json(admin, "/api/web/scene");
            var splitInput = JsonSerializer.Serialize(new { baseRevision = splitScene.GetProperty("revision").GetInt64(), sourceId = nodeId });
            await Json(other, "/api/web/worktree/split", splitInput, 403);
            await Json(admin, "/api/web/worktree/split", "{\"baseRevision\":0,\"sourceId\":\"" + nodeId + "\"}", 409);
            await Json(admin, "/api/web/worktree/split", splitInput);
            var splitDisk = JsonDocument.Parse(File.ReadAllBytes(canvasPath)).RootElement;
            Assert(splitDisk.GetProperty("Canvas").GetProperty("WorkTree").GetArrayLength() > 0, "split creates authoritative worktree items");
            Assert(splitDisk.GetProperty("Canvas").GetProperty("Nodes").GetArrayLength() > JsonDocument.Parse(treeBefore).RootElement.GetProperty("Canvas").GetProperty("Nodes").GetArrayLength(), "split creates chapter nodes");
            treePlan = await Json(admin, "/api/web/worktree/plan?direction=Both");
            await Json(admin, "/api/web/worktree/apply", JsonSerializer.Serialize(new { baseRevision = treePlan.GetProperty("revision").GetInt64(), direction = "Both", baselineHash = treePlan.GetProperty("plan").GetProperty("baselineHash").GetString(), confirmed = true }));
            history = await Json(admin, "/api/web/canvas/history");
            Assert(history.GetProperty("undo").GetInt32() > 0, "tree operations enter ordinary history");
            File.WriteAllBytes(canvasPath, treeBefore);
            Console.WriteLine("Worktree regression passed: shared split and sync, preview byte preservation, permissions, stale plan and revision rejection, actual chapter nodes and history capture");
            var beforeActions = File.ReadAllBytes(canvasPath);
            var proposal = await Proposal();
            Assert(captured.Last().ToString().Contains("Selected context body"), "selected node context sent to model");
            Assert(beforeActions.SequenceEqual(File.ReadAllBytes(canvasPath)), "approval generation does not write");
            var actionPreview = await Json(admin, ActionPath(proposal, "preview"), "{}");
            Assert(actionPreview.GetProperty("sessionId").GetString() == proposal.Session && actionPreview.GetProperty("messageId").GetString() == proposal.Message, "preview ownership identity");
            var diff = actionPreview.GetProperty("diff");
            foreach (var field in new[] { "addedNodes", "updatedNodes", "removedNodes", "addedEdges", "removedEdges" })
                Assert(diff.GetProperty(field).ValueKind == JsonValueKind.Array, "preview diff array " + field);
            var trialCanvas = JsonSerializer.Deserialize<YEEYEEYEE.Desktop.RecentCanvasState>(actionPreview.GetProperty("after").GetRawText())!.Canvas;
            var expectedNode = YEEYEEYEE.Desktop.NodeProjection.ProjectRecords(trialCanvas.Nodes, trialCanvas)
                .Select(record => JsonSerializer.SerializeToElement(record)).Single(record => record.GetProperty("recordId").GetString() == nodeId.ToString());
            Assert(diff.GetProperty("updatedNodes").GetArrayLength() == 1 && System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(diff.GetProperty("updatedNodes")[0].GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(expectedNode.GetRawText())), "diff matches existing trial Web projection");
            Assert(new[] { "addedNodes", "removedNodes", "addedEdges", "removedEdges" }.All(field => diff.GetProperty(field).GetArrayLength() == 0), "unchanged arrays empty");
            foreach (var operation in new[] { "preview", "commit", "undo" })
            {
                await Json(other, ActionPath(proposal, operation), "{}", 404);
                await Json(admin, $"/api/web/chat/sessions/{id}/actions/{proposal.Message}/{operation}", "{}", 404);
            }
            Assert(beforeActions.SequenceEqual(File.ReadAllBytes(canvasPath)), "preview does not write");
            await Json(admin, ActionPath(proposal, "commit"), "{}");
            Assert(File.ReadAllText(canvasPath).Contains("Agent changed"), "commit writes actual desktop canvas");
            await Json(admin, ActionPath(proposal, "commit"), "{}", 409);
            await Json(admin, ActionPath(proposal, "undo"), "{}");
            Assert(File.ReadAllText(canvasPath).Contains("Selected context body"), "undo restores original content");
            var addedProposal = await Proposal(output: "```json\n" + JsonSerializer.Serialize(new { actions = new object[] {
                new { kind = "create_node", title = "Trial added node", content = "Trial content" },
                new { kind = "create_edge", source = nodeId.ToString(), target = "Trial added node" } } }) + "\n```");
            var addedBytes = File.ReadAllBytes(canvasPath);
            var addedPreview = await Json(admin, ActionPath(addedProposal, "preview"), "{}");
            var addedDiff = addedPreview.GetProperty("diff");
            var addedCanvas = JsonSerializer.Deserialize<YEEYEEYEE.Desktop.RecentCanvasState>(addedPreview.GetProperty("after").GetRawText())!.Canvas;
            Assert(addedDiff.GetProperty("addedNodes").GetArrayLength() == 1 && addedDiff.GetProperty("addedEdges").GetArrayLength() == 1, "trial adds node and edge");
            var addedId = addedDiff.GetProperty("addedNodes")[0].GetProperty("recordId").GetString();
            Assert(addedCanvas.Nodes.Any(node => node.Id.ToString() == addedId) && addedCanvas.Edges.Any(edge =>
                edge.Id.ToString() == addedDiff.GetProperty("addedEdges")[0].GetProperty("edgeId").GetString() && edge.TargetNodeId.ToString() == addedId), "diff IDs equal single trial IDs");
            Assert(addedBytes.SequenceEqual(File.ReadAllBytes(canvasPath)), "adding preview never writes");
            await Json(admin, ActionPath(addedProposal, "commit"), "{}");
            var removalProposal = await Proposal(output: "```json\n" + JsonSerializer.Serialize(new { actions = new[] { new { kind = "delete_node", target = "Trial added node" } } }) + "\n```");
            var removalBytes = File.ReadAllBytes(canvasPath);
            var removalPreview = await Json(admin, ActionPath(removalProposal, "preview"), "{}");
            var removalDiff = removalPreview.GetProperty("diff");
            var removalBefore = JsonSerializer.Deserialize<YEEYEEYEE.Desktop.RecentCanvasState>(removalPreview.GetProperty("before").GetRawText())!.Canvas;
            Assert(removalDiff.GetProperty("removedNodes").GetArrayLength() == 1 && removalDiff.GetProperty("removedEdges").GetArrayLength() == 1, "delete preview includes removed node and incident edge");
            Assert(removalBefore.Nodes.Any(node => node.Id.ToString() == removalDiff.GetProperty("removedNodes")[0].GetProperty("recordId").GetString()) &&
                removalBefore.Edges.Any(edge => edge.Id.ToString() == removalDiff.GetProperty("removedEdges")[0].GetProperty("edgeId").GetString()), "removed records use original trial IDs");
            Assert(removalBytes.SequenceEqual(File.ReadAllBytes(canvasPath)), "removal preview never writes");
            await Json(admin, ActionPath(addedProposal, "undo"), "{}");
            var readOnly = await Proposal("readonly");
            var unchanged = File.ReadAllBytes(canvasPath);
            await Json(admin, ActionPath(readOnly, "commit"), "{}", 403);
            Assert(unchanged.SequenceEqual(File.ReadAllBytes(canvasPath)), "readonly never commits");
            var stale = await Proposal();
            await Proposal("auto");
            await Json(admin, ActionPath(stale, "commit"), "{}", 409);
            var invalid = await Proposal(output: "```json\n" + JsonSerializer.Serialize(new { actions = new[] { new { kind = "update_node", target = nodeId.ToString(), content = "must roll back" }, new { kind = "update_node", target = Guid.NewGuid().ToString(), content = "missing" } } }) + "\n```");
            unchanged = File.ReadAllBytes(canvasPath);
            await Json(admin, ActionPath(invalid, "commit"), "{}", 409);
            Assert(unchanged.SequenceEqual(File.ReadAllBytes(canvasPath)), "failed batch leaves disk unchanged");
            var ask = await Proposal("auto", "```json\n{\"ask\":{\"question\":\"确认吗？\",\"options\":[\"确认\",\"修改\"]}}\n```");
            Assert((await Json(admin, "/api/web/chat/sessions/" + ask.Session)).GetProperty("messages")[1].GetProperty("ask").GetProperty("options").GetArrayLength() == 2, "desktop ask parsed");
            await Json(admin, "/api/auth/users", "{\"username\":\"chateditor\",\"password\":\"Password123!\",\"role\":\"Editor\"}");
            using var editor = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = admin.BaseAddress };
            await Json(editor, "/api/auth/login", "{\"username\":\"chateditor\",\"password\":\"Password123!\"}");
            var locked = await Proposal();
            var lease = await Json(editor, "/api/web/edits", JsonSerializer.Serialize(new { scope = "node", targetId = nodeId }));
            await Json(admin, ActionPath(locked, "commit"), "{}", 409);
            await Json(editor, "/api/web/edits/" + lease.GetProperty("lease").GetProperty("leaseId").GetString(), "{}", 200, "DELETE");
            var ownLease = await Json(admin, "/api/web/edits", JsonSerializer.Serialize(new { scope = "node", targetId = nodeId }));
            var ownLeaseId = ownLease.GetProperty("lease").GetProperty("leaseId").GetString();
            await Json(admin, ActionPath(locked, "commit"), "{}");
            Assert((await Json(admin, "/api/web/edits")).GetProperty("leases").EnumerateArray().Any(l => l.GetProperty("leaseId").GetString() == ownLeaseId), "commit preserves own node lease");
            await Json(admin, "/api/web/edits/" + ownLeaseId, "{}", 200, "DELETE");
            await Json(admin, ActionPath(locked, "preview"), "{}", 409);
            var askActions = await Proposal("auto", "```json\n" + JsonSerializer.Serialize(new { ask = new { question = "确认修改？", options = new[] { "确认", "修改" } }, actions = new[] { new { kind = "update_node", target = nodeId.ToString(), content = "needs confirmation" } } }) + "\n```");
            unchanged = File.ReadAllBytes(canvasPath);
            Assert((await Json(admin, "/api/web/chat/sessions/" + askActions.Session)).GetProperty("messages")[1].GetProperty("actionState").GetString() == "preview", "ask prevents automatic commit");
            var blockedSave = await Proposal();
            using (var heldSaveLock = new FileStream(canvasPath + ".web.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                await Json(admin, ActionPath(blockedSave, "commit"), "{}", 503);
                Assert(unchanged.SequenceEqual(File.ReadAllBytes(canvasPath)), "failed save leaves authoritative bytes unchanged");
            }
            await Proposal("auto");
            await Json(admin, ActionPath(locked, "undo"), "{}", 409);
            var job = await Json(admin, "/api/web/skills/comfyui.text-to-image/invoke", "{\"prompt\":\"offline\",\"idempotencyKey\":\"chat-job\"}");
            var jid = job.GetProperty("jobId").GetString();
            await Json(editor, "/api/web/jobs/" + jid, status: 404);
            await Json(editor, $"/api/web/jobs/{jid}/cancel", "{}", 404);
            await Json(editor, $"/api/web/jobs/{jid}/retry", "{}", 404);
            await Json(editor, $"/api/web/jobs/{jid}/outputs/0", status: 404);
            await Json(editor, $"/api/web/chat/sessions/{locked.Session}/jobs/{jid}", "{}", 404);
            var linked = await Json(admin, $"/api/web/chat/sessions/{locked.Session}/jobs/{jid}", "{}");
            Assert(linked.GetProperty("jobIds")[0].GetString() == jid, "owned job linked to chat");
            await Json(admin, $"/api/web/chat/sessions/{locked.Session}/jobs/{jid}", "{}");
            Assert((await Json(admin, "/api/web/chat/sessions/" + locked.Session)).GetProperty("jobIds").GetArrayLength() == 1, "link retry is idempotent");
            await Json(admin, $"/api/web/chat/sessions/{locked.Session}/jobs/{Guid.NewGuid()}", "{}", 404);
            string SkillOutput(string target = "comfyui.text-to-image", bool askFirst = false) => "```json\n" + JsonSerializer.Serialize(new { actions = new[] { new { kind = "invoke_skill", target, content = "model offline prompt" } }, ask = askFirst ? new { question = "确认生成？", options = new[] { "确认" } } : null }) + "\n```";
            string SkillPath((string Session, string Message) p) => $"/api/web/chat/sessions/{p.Session}/skills/{p.Message}/invoke";
            var skillProposal = await Proposal(output: SkillOutput());
            var skillSession = await Json(admin, "/api/web/chat/sessions/" + skillProposal.Session);
            Assert(skillSession.GetProperty("jobIds").GetArrayLength() == 1 && skillSession.GetProperty("messages")[1].GetProperty("skillState").GetString() is "submitted" or "completed" or "failed", "approval executes non-canvas skill without separate approval");
            await Json(other, SkillPath(skillProposal), "{}", 404);
            var approvedJob = await Json(admin, "/api/web/jobs/" + skillSession.GetProperty("jobIds")[0].GetString());
            Assert(approvedJob.GetProperty("targetNodeId").GetGuid() == nodeId && approvedJob.GetProperty("attachmentState").GetString() == "none", "conversation skill never passes selected target to background attachment");
            for (var i = 0; i < 100; i++)
            {
                skillSession = await Json(admin, "/api/web/chat/sessions/" + skillProposal.Session);
                if (skillSession.GetProperty("messages")[1].GetProperty("skillState").GetString() == "failed") break;
                await Task.Delay(50);
            }
            await Json(admin, SkillPath(skillProposal), "{\"aspectRatio\":\"16:9\"}");
            var resubmittedSession = await Json(admin, "/api/web/chat/sessions/" + skillProposal.Session);
            Assert(resubmittedSession.GetProperty("jobIds").GetArrayLength() == 2 &&
                resubmittedSession.GetProperty("jobIds")[0].GetGuid() != resubmittedSession.GetProperty("jobIds")[1].GetGuid(),
                "explicit failed skill resubmission creates a new job and retains the old job");
            var autoSkill = await Proposal("auto", SkillOutput());
            skillSession = await Json(admin, "/api/web/chat/sessions/" + autoSkill.Session);
            Assert(skillSession.GetProperty("jobIds").GetArrayLength() == 1 && skillSession.GetProperty("messages")[1].GetProperty("skillState").GetString() is "submitted" or "completed" or "failed", "model auto submits and links");
            var autoJobId = skillSession.GetProperty("jobIds")[0].GetString();
            for (var i = 0; i < 100; i++)
            {
                var result = await Json(admin, "/api/web/jobs/" + autoJobId);
                if (result.GetProperty("state").GetString() == "Failed") { Assert(result.GetProperty("outputs").GetArrayLength() == 0, "failed backend has no false output"); break; }
                if (i == 99) throw new Exception("offline model skill did not fail");
                await Task.Delay(50);
            }
            var readonlyBytes = File.ReadAllBytes(canvasPath);
            var readonlySkill = await Proposal("readonly", SkillOutput());
            skillSession = await Json(admin, "/api/web/chat/sessions/" + readonlySkill.Session);
            Assert(skillSession.GetProperty("jobIds").GetArrayLength() == 1 && skillSession.GetProperty("messages")[1].GetProperty("skillState").GetString() is "submitted" or "completed" or "failed", "readonly executes non-canvas skill");
            Assert(readonlyBytes.SequenceEqual(File.ReadAllBytes(canvasPath)), "readonly skill preserves canvas bytes");
            // 后台可能已把失败状态写回；重复发起的幂等性由前面的关联与任务测试覆盖。
            var askSkill = await Proposal("auto", SkillOutput(askFirst: true));
            Assert((await Json(admin, "/api/web/chat/sessions/" + askSkill.Session)).GetProperty("jobIds").GetArrayLength() == 0, "ask prevents skill auto submit");
            var unknownSkill = await Proposal("auto", SkillOutput("desktop.unsupported"));
            Assert((await Json(admin, "/api/web/chat/sessions/" + unknownSkill.Session)).GetProperty("messages")[1].GetProperty("skillError").GetString()!.Contains("SKILL_NOT_FOUND"), "unknown skill explicit error");
            var deniedSkill = await Proposal("auto", SkillOutput("web.text-to-video"));
            Assert((await Json(admin, "/api/web/chat/sessions/" + deniedSkill.Session)).GetProperty("messages")[1].GetProperty("skillError").GetString()!.Contains("SKILL_NOT_APPROVED"), "unapproved skill explicit error");
            var unsupportedAction = await Proposal("auto", "```json\n{\"actions\":[{\"kind\":\"write_file\",\"path\":\"unsupported.txt\",\"content\":\"test\"}]}\n```");
            Assert((await Json(admin, "/api/web/chat/sessions/" + unsupportedAction.Session)).GetProperty("messages")[1].GetProperty("actionErrors")[0].GetString()!.Contains("write_file"), "unsupported canvas action names exact limitation");
            var extraSkill = await Proposal("auto", "```json\n{\"actions\":[{\"kind\":\"invoke_skill\",\"target\":\"comfyui.text-to-image\",\"content\":\"test\",\"width\":512}]}\n```");
            Assert((await Json(admin, "/api/web/chat/sessions/" + extraSkill.Session)).GetProperty("messages")[1].GetProperty("skillError").GetString()!.Contains("SKILL_INPUT_UNSUPPORTED"), "unsupported parameters never silently ignored");
            var multiSkill = await Proposal("auto", "```json\n{\"actions\":[{\"kind\":\"invoke_skill\",\"target\":\"comfyui.text-to-image\",\"content\":\"one\"},{\"kind\":\"invoke_skill\",\"target\":\"comfyui.text-to-image\",\"content\":\"two\"}]}\n```");
            Assert((await Json(admin, "/api/web/chat/sessions/" + multiSkill.Session)).GetProperty("messages")[1].GetProperty("skillState").GetString() == "blocked", "multiple skills explicitly blocked");
            foreach (var canvasMode in new[] { "readonly", "approval", "auto" })
            {
                var mixedBefore = File.ReadAllBytes(canvasPath);
                var mixed = await Proposal(canvasMode, "```json\n" + JsonSerializer.Serialize(new { actions = new object[] {
                    new { kind = "invoke_skill", target = "comfyui.text-to-image", content = "mixed generation" },
                    new { kind = "update_node", target = nodeId.ToString(), content = "mixed " + canvasMode }
                } }) + "\n```");
                var mixedReply = (await Json(admin, "/api/web/chat/sessions/" + mixed.Session)).GetProperty("messages")[1];
                Assert(mixedReply.GetProperty("skillState").GetString() is "submitted" or "completed" or "failed", canvasMode + " mixed reply executes skill");
                Assert(mixedReply.GetProperty("actionState").GetString() == (canvasMode == "auto" ? "committed" : "preview"), canvasMode + " mixed reply respects canvas approval");
                if (canvasMode == "auto")
                    Assert(File.ReadAllText(canvasPath).Contains("mixed auto"), "mixed auto writes canvas");
                else
                    Assert(mixedBefore.SequenceEqual(File.ReadAllBytes(canvasPath)), canvasMode + " mixed reply preserves canvas before approval");
                if (canvasMode == "approval")
                {
                    // 生成产物回挂会在服务端往同一条回复追加 attach_asset：等技能落定，再按**最新动作**
                    // 预检一次并提交（与前端同一条路——旧批准不会把没看见的动作一起执行掉）。
                    await WaitSkillSettled(mixed);
                    await Json(admin, ActionPath(mixed, "preview"), "{}");
                    await Json(admin, ActionPath(mixed, "commit"), "{}");
                    Assert(File.ReadAllText(canvasPath).Contains("mixed approval"), "mixed approval uses existing commit");
                }
                if (canvasMode == "readonly")
                {
                    // 等完成回调释放会话锁后再断言只读模式被拒；否则会偶发先撞上 409 CHAT_BUSY。
                    await WaitSkillSettled(mixed);
                    await Json(admin, ActionPath(mixed, "commit"), "{}", 403);
                }
            }
            modelText = "回答"; await Send(autoSkill.Session, "{\"text\":\"任务结果是什么\"}");
            Assert(captured.Last().ToString().Contains("Failed") && captured.Last().ToString().Contains(autoJobId!), "actual job failure enters model context");
            Console.WriteLine("Model skill regression passed: approval, auto submission, server-side link, readonly, ask gating, unknown/unapproved errors, duplicate approval, offline backend failure, idempotent link recovery and owner isolation");
            modelText = "回答";
            var chapterOne = Guid.NewGuid(); var chapterTwo = Guid.NewGuid();
            var scopedCanvas = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(canvasPath))!;
            scopedCanvas["Canvas"]!["WorkTree"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new[] {
                new { Id = chapterOne, Kind = 6, Name = "第一章", Order = 10 }, new { Id = chapterTwo, Kind = 6, Name = "第二章", Order = 20 } }));
            scopedCanvas["Canvas"]!["Nodes"]![0]!["WorkTreeItemId"] = chapterOne.ToString();
            scopedCanvas["Canvas"]!["Nodes"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { Id = Guid.NewGuid(), Category = 0, Title = "Outside chapter", Content = "Outside chapter secret", WorkTreeItemId = chapterTwo })));
            File.WriteAllText(canvasPath, scopedCanvas.ToJsonString());
            var scopedSession = (await Json(admin, "/api/web/chat/sessions", "{}")).GetProperty("id").GetString()!;
            var scopedDone = await Send(scopedSession, JsonSerializer.Serialize(new { text = "阅读本章", chapterId = chapterOne, nodeId, source = "node-menu", targetNodeTitle = "Bound title", mode = "approval" }));
            Assert(scopedDone.GetProperty("messages").EnumerateArray().All(message => message.GetProperty("targetChapterId").GetString() == chapterOne.ToString() && message.GetProperty("source").GetString() == "node-menu"), "both roles persist chapter and source");
            // 模型上下文必须带着目标节点的稳定 id（标题可能重名）：身份段与清单行都要有。
            // 请求体里的非 ASCII 是 \uXXXX 转义过的（JsonElement.ToString 也是），所以先解回可读文本再找。
            static string Readable(JsonElement payload) => System.Text.RegularExpressions.Regex.Unescape(payload.ToString());
            Assert(captured.Any(payload => Readable(payload).Contains("本次请求的目标节点") && Readable(payload).Contains(nodeId.ToString("N"))),
                "node-menu model context states the request target explicitly with the node id");
            mode = "fail";
            var scopedFailed = await Send(scopedSession, JsonSerializer.Serialize(new { text = "重试本章", chapterId = chapterOne, nodeId, source = "node-menu", targetNodeTitle = "Bound title", mode = "readonly" }));
            mode = "success";
            var scopedRetried = await Send(scopedSession, JsonSerializer.Serialize(new { retry = true, chapterId = chapterTwo, nodeId = Guid.NewGuid(), mode = "auto", source = "changed" }));
            foreach (var field in new[] { "id", "targetNodeId", "targetChapterId", "baseRevision", "mode", "source", "targetNodeTitle" })
                Assert(scopedRetried.GetProperty("messages")[3].GetProperty(field).ToString() == scopedFailed.GetProperty("messages")[3].GetProperty(field).ToString(), "scoped retry preserves " + field);
            // 重试时前端带上的是**当时选中的另一个节点**：原请求的目标不能因此被改写，
            // 模型上下文同样要按原目标重建。
            Assert(Readable(captured.Where(payload => Readable(payload).Contains("重试本章")).Last()).Contains(nodeId.ToString("N")),
                "retry keeps the original request target in the model context");
            Assert(captured.Last().ToString().Contains(chapterOne.ToString()) && !captured.Last().ToString().Contains("Outside chapter secret"), "chapter filters node context by stable identity");
            mode = "fail";
            var staleRetry = await Send(scopedSession, JsonSerializer.Serialize(new { text = "过期重试", chapterId = chapterOne, nodeId, source = "node-menu" }));
            mode = "success";
            var boundHistory = staleRetry.ToString();
            var savedCanvasBytes = File.ReadAllBytes(canvasPath);
            scopedCanvas["Canvas"]!["Nodes"]![0]!["Title"] = "Changed after failure";
            File.WriteAllText(canvasPath, scopedCanvas.ToJsonString());
            await Json(admin, $"/api/web/chat/sessions/{scopedSession}/messages", "{\"retry\":true}", 409);
            Assert(boundHistory == (await Json(admin, "/api/web/chat/sessions/" + scopedSession)).ToString(), "stale retry preserves original failed message");
            File.WriteAllBytes(canvasPath, savedCanvasBytes);
            scopedCanvas = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(canvasPath))!;
            scopedCanvas["FormatVersion"] = 999;
            File.WriteAllText(canvasPath, scopedCanvas.ToJsonString());
            await Json(admin, $"/api/web/chat/sessions/{scopedSession}/messages", JsonSerializer.Serialize(new { text = "只读画布", nodeId, source = "node-menu" }), 409);
            Assert(boundHistory == (await Json(admin, "/api/web/chat/sessions/" + scopedSession)).ToString(), "node menu rejects nonwritable canvas without saving history");
            File.WriteAllBytes(canvasPath, savedCanvasBytes);
            scopedCanvas = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(canvasPath))!;
            var scopedHistory = (await Json(admin, "/api/web/chat/sessions/" + scopedSession)).ToString();
            await Json(admin, $"/api/web/chat/sessions/{scopedSession}/messages", "{\"text\":\"invalid\",\"chapterId\":\"bad-id\"}", 409);
            Assert(scopedHistory == (await Json(admin, "/api/web/chat/sessions/" + scopedSession)).ToString(), "rejected context does not change persisted history");
            scopedCanvas["Canvas"]!["Nodes"]![0]!["IsLocked"] = true;
            File.WriteAllText(canvasPath, scopedCanvas.ToJsonString());
            var lockedNode = await Proposal();
            var lockedBytes = File.ReadAllBytes(canvasPath);
            await Json(admin, ActionPath(lockedNode, "commit"), "{}", 409);
            Assert(lockedBytes.SequenceEqual(File.ReadAllBytes(canvasPath)), "finalized node refuses actions without changing bytes");
            var attachmentSession = (await Json(admin, "/api/web/chat/sessions", "{}")).GetProperty("id").GetString()!;
            async Task<string?> Upload(HttpClient client, byte[] bytes, string type, int expected = 200)
            {
                using var content = new ByteArrayContent(bytes); content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
                using var response = await client.PostAsync($"/api/web/chat/sessions/{attachmentSession}/attachments?name=reference.txt", content);
                var result = await response.Content.ReadAsStringAsync(); Assert((int)response.StatusCode == expected, "attachment upload " + result);
                return expected == 200 ? JsonDocument.Parse(result).RootElement.GetProperty("id").GetString() : null;
            }
            var textAttachment = await Upload(admin, Encoding.UTF8.GetBytes("附件真实正文"), "text/plain");
            var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
            var imageAttachment = await Upload(admin, imageBytes, "image/png");
            await Upload(admin, new byte[65537], "text/plain", 413);
            await Upload(admin, Encoding.UTF8.GetBytes("not image"), "image/png", 415);
            await Upload(admin, new byte[] { 255 }, "text/plain", 415);
            await Upload(other, imageBytes, "image/png", 404);
            await Json(other, $"/api/web/chat/sessions/{attachmentSession}/attachments/{imageAttachment}", status: 404);
            await Json(admin, $"/api/web/chat/sessions/{id}/messages", JsonSerializer.Serialize(new { text = "跨会话", attachmentIds = new[] { imageAttachment } }), 400);
            // 更多文件类型：类型、签名、该类型上限与媒体种类都走**同一份服务端注册表**，
            // 上传、下载、参考图判定与前端可选类型不会各写一份而走散。
            var webpBytes = new byte[32];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(webpBytes, 0); Encoding.ASCII.GetBytes("WEBP").CopyTo(webpBytes, 8);
            var gifBytes = Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[] { 0x2C, 0x00, 0x00, 0x00 }).ToArray();
            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%离线用例\n%%EOF\n");
            var mp4Bytes = new byte[32];
            Encoding.ASCII.GetBytes("ftyp").CopyTo(mp4Bytes, 4); Encoding.ASCII.GetBytes("isom").CopyTo(mp4Bytes, 8);
            var mp3Bytes = Encoding.ASCII.GetBytes("ID3").Concat(new byte[] { 0x03, 0x00, 0x00, 0x00 }).ToArray();
            var markdownBytes = Encoding.UTF8.GetBytes("# 设定\n正文");
            var webpAttachment = await Upload(admin, webpBytes, "image/webp");
            var gifAttachment = await Upload(admin, gifBytes, "image/gif");
            var pdfAttachment = await Upload(admin, pdfBytes, "application/pdf");
            var mp4Attachment = await Upload(admin, mp4Bytes, "video/mp4");
            var mp3Attachment = await Upload(admin, mp3Bytes, "audio/mpeg");
            var markdownAttachment = await Upload(admin, markdownBytes, "text/markdown");
            await Upload(admin, Encoding.UTF8.GetBytes("not a pdf"), "application/pdf", 415);
            await Upload(admin, gifBytes, "image/webp", 415);
            await Upload(admin, new byte[5242881], "image/png", 413);
            await Upload(admin, mp4Bytes, "video/x-matroska", 415);
            foreach (var (attachmentId, expectedType, expectedBytes) in new (string?, string, byte[])[] {
                (webpAttachment, "image/webp", webpBytes), (gifAttachment, "image/gif", gifBytes),
                (pdfAttachment, "application/pdf", pdfBytes), (mp4Attachment, "video/mp4", mp4Bytes),
                (mp3Attachment, "audio/mpeg", mp3Bytes), (markdownAttachment, "text/markdown", markdownBytes) })
            {
                using var download = await admin.GetAsync($"/api/web/chat/sessions/{attachmentSession}/attachments/{attachmentId}");
                Assert(download.IsSuccessStatusCode && download.Content.Headers.ContentType?.MediaType == expectedType
                    && (await download.Content.ReadAsByteArrayAsync()).SequenceEqual(expectedBytes),
                    "new attachment type keeps MIME and bytes: " + expectedType);
            }
            using (var ranged = new HttpRequestMessage(HttpMethod.Get, $"/api/web/chat/sessions/{attachmentSession}/attachments/{mp4Attachment}"))
            {
                ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);
                using var partial = await admin.SendAsync(ranged);
                Assert(partial.StatusCode == HttpStatusCode.PartialContent, "media attachments serve range requests for in-page playback");
            }
            // 非图片类型不能当参考图：明确拒绝，不静默过滤掉再照跑。
            await Json(admin, "/api/web/skills/comfyui.text-to-image/invoke", JsonSerializer.Serialize(new { prompt = "video reference", idempotencyKey = "reference-video", chatSessionId = attachmentSession, referenceAttachmentIds = new[] { mp4Attachment } }), 400);
            // 可选类型与参数声明由服务端下发（前端的 accept 与编辑器照它渲染）。
            var skillCatalog = await Json(admin, "/api/web/skills");
            Assert(skillCatalog.GetProperty("attachments").EnumerateArray().Any(type => type.GetProperty("mime").GetString() == "application/pdf" && !type.GetProperty("referenceable").GetBoolean())
                && skillCatalog.GetProperty("attachmentAccept").GetString()!.Contains("image/webp", StringComparison.Ordinal)
                && skillCatalog.GetProperty("skills").EnumerateArray().Any(skill => skill.GetProperty("id").GetString() == "comfyui.text-to-image"
                    && skill.GetProperty("parameters").EnumerateArray().Any(parameter => parameter.GetProperty("name").GetString() == "seed")),
                "skill catalog declares attachment types and parameter specs");
            Console.WriteLine("Chat attachment type regression passed: webp/gif/pdf/mp4/mp3/markdown upload, signature/limit rejection, MIME-preserving download, range serving, non-image reference refusal and server-declared catalog");
            modelText = "回答";
            await Send(attachmentSession, JsonSerializer.Serialize(new { text = "阅读附件", attachmentIds = new[] { textAttachment, imageAttachment } }));
            Assert(captured.Last().GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("content").ValueKind == JsonValueKind.Array && m.GetProperty("content").EnumerateArray().Any(p => p.TryGetProperty("text", out var text) && text.GetString()!.Contains("附件真实正文"))) && captured.Last().ToString().Contains("data:image/png;base64,"), "actual model HTTP request includes text and image bytes");
            Config(anthropic: true);
            await Send(attachmentSession, "{\"text\":\"再次阅读\"}");
            Assert(captured.Last().ToString().Contains("base64") && captured.Last().ToString().Contains("image/png"), "Anthropic image history retained"); Config();
            var referenceJob = await Json(admin, "/api/web/skills/comfyui.text-to-image/invoke", JsonSerializer.Serialize(new { prompt = "reference test", idempotencyKey = "reference-test", chatSessionId = attachmentSession, referenceAttachmentIds = new[] { imageAttachment } }));
            await Json(admin, "/api/web/skills/comfyui.text-to-image/invoke", JsonSerializer.Serialize(new { prompt = "reference test", idempotencyKey = "reference-missing", chatSessionId = id, referenceAttachmentIds = new[] { imageAttachment } }), 400);
            Stop(); await Start();
            using (var response = await admin.GetAsync($"/api/web/chat/sessions/{attachmentSession}/attachments/{imageAttachment}"))
                Assert(response.IsSuccessStatusCode && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(imageBytes), "attachment bytes survive restart");
            Console.WriteLine("Attachment HTTP regression passed: upload, UTF-8 and signature/type/size boundaries, owner/session isolation, OpenAI/Anthropic actual request bytes, reference skill submission and restart persistence");
            var libraryBefore = await Json(admin, "/api/web/assets");
            var createEntityBody = JsonSerializer.Serialize(new { operation = "createEntity", name = "HTTP角色", kind = "Character", baseRevision = libraryBefore.GetProperty("revision").GetInt64() });
            await Json(other, "/api/web/entities/manage", createEntityBody, 403);
            await Json(admin, "/api/web/entities/manage", createEntityBody);
            await Json(admin, "/api/web/entities/manage", createEntityBody, 409);
            var libraryAfter = await Json(admin, "/api/web/assets");
            var createdEntity = libraryAfter.GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "HTTP角色");
            Assert(createdEntity.GetProperty("variants")[0].GetProperty("versions").GetArrayLength() == 1, "shared entity initial version");
            await Json(admin, "/api/web/entities/manage", JsonSerializer.Serialize(new { operation = "createVariant", name = "夜景", entityId = createdEntity.GetProperty("id").GetString(), baseRevision = libraryAfter.GetProperty("revision").GetInt64() }));
            Assert((await Json(admin, "/api/web/assets")).GetProperty("entities")[0].GetProperty("variants").GetArrayLength() == 2, "actual authority library variant persisted");
            var entityKey = createdEntity.GetProperty("id").GetGuid();
            var variantKey = createdEntity.GetProperty("variants")[0].GetProperty("id").GetGuid();
            var initialVersionKey = createdEntity.GetProperty("variants")[0].GetProperty("versions")[0].GetProperty("id").GetGuid();
            async Task<JsonElement> EditEntity(string operation, object? extra = null, HttpClient? client = null, int expected = 200)
            {
                var snapshot = await Json(admin, "/api/web/assets");
                var body = extra is null ? new System.Text.Json.Nodes.JsonObject() : JsonSerializer.SerializeToNode(extra)!.AsObject();
                body["operation"] = operation;
                body["baseRevision"] = snapshot.GetProperty("revision").GetInt64();
                body["entityId"] = entityKey.ToString();
                body["variantId"] = variantKey.ToString();
                return await Json(client ?? admin, "/api/web/entities/edit", body.ToJsonString(), expected);
            }
            await EditEntity("editEntity", new { core = "HTTP核心描述", aliases = "HTTP别名" }, other, 403);
            await EditEntity("editEntity", new { core = "HTTP核心描述", aliases = "HTTP别名" });
            await EditEntity("editVariant", new { description = "HTTP变体描述" });
            await EditEntity("commitVersion", new { note = "HTTP版本提交" });
            var editedEntity = (await Json(admin, "/api/web/assets")).GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == entityKey);
            Assert(editedEntity.GetProperty("core").GetString() == "HTTP核心描述" && editedEntity.GetProperty("aliases").GetString() == "HTTP别名", "entity descriptions persisted");
            Assert(editedEntity.GetProperty("variants")[0].GetProperty("versions").GetArrayLength() == 2, "shared variant commit persisted");
            await EditEntity("commitVersion", expected: 409);
            await EditEntity("rollbackVersion", new { versionId = initialVersionKey });
            editedEntity = (await Json(admin, "/api/web/assets")).GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == entityKey);
            Assert(editedEntity.GetProperty("variants")[0].GetProperty("description").GetString() == "", "rollback restores shared initial description");
            await EditEntity("deleteVersion", new { versionId = initialVersionKey }, expected: 409);
            await EditEntity("addMedia", new { name = "伪造PNG", mediaType = "image/png", data = Convert.ToBase64String(Encoding.UTF8.GetBytes("not an image")) }, expected: 400);
            await EditEntity("addMedia", new { name = "HTTP媒体", mediaType = "image/png", data = Convert.ToBase64String(imageBytes) });
            editedEntity = (await Json(admin, "/api/web/assets")).GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == entityKey);
            var mediaKey = editedEntity.GetProperty("variants")[0].GetProperty("attachments")[0].GetProperty("id").GetGuid();
            Assert(editedEntity.GetProperty("variants")[0].GetProperty("attachments")[0].GetProperty("reference").GetString() == $"/api/web/entities/{entityKey}/variants/{variantKey}/media/{mediaKey}", "media reference is safe URL");
            Assert(!editedEntity.GetRawText().Contains("data:image/"), "asset response excludes inline media data");
            using (var response = await admin.GetAsync($"/api/web/entities/{entityKey}/variants/{variantKey}/media/{mediaKey}"))
                Assert(response.IsSuccessStatusCode && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(imageBytes), "entity media byte endpoint");
            await EditEntity("renameMedia", new { attachmentId = mediaKey, name = "HTTP媒体重命名" });
            await EditEntity("commitVersion", new { note = "带媒体的版本" });
            editedEntity = (await Json(admin, "/api/web/assets")).GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == entityKey);
            var mediaVersionKey = editedEntity.GetProperty("variants")[0].GetProperty("versions")[2].GetProperty("id").GetGuid();
            await EditEntity("removeMedia", new { attachmentId = mediaKey });
            await Json(admin, $"/api/web/entities/{entityKey}/variants/{variantKey}/media/{mediaKey}", status: 404);
            using (var response = await admin.GetAsync($"/api/web/entities/{entityKey}/variants/{variantKey}/media/{mediaKey}?versionId={mediaVersionKey}"))
                Assert(response.IsSuccessStatusCode && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(imageBytes), "history retains media bytes after detach");
            await EditEntity("addMedia", new { name = "bad", mediaType = "text/html", data = "PHNjcmlwdD4=" }, expected: 400);
            await EditEntity("deleteVariant", client: other, expected: 403);
            var referenceCanvasPath = Path.Combine(project, "canvases", "reference.json");
            var referenceCanvas = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(canvasPath))!;
            referenceCanvas["Canvas"]!["Nodes"]![0]!["References"] = JsonSerializer.SerializeToNode(new[] { new { EntityId = entityKey, VariantId = variantKey, VariantVersionId = initialVersionKey } });
            File.WriteAllText(referenceCanvasPath, referenceCanvas.ToJsonString());
            var protectedLibraryBytes = File.ReadAllBytes(entitiesPath);
            await EditEntity("deleteEntity", expected: 409);
            await EditEntity("deleteVariant", expected: 409);
            await EditEntity("deleteVersion", new { versionId = initialVersionKey }, expected: 409);
            Assert(protectedLibraryBytes.SequenceEqual(File.ReadAllBytes(entitiesPath)), "cross-canvas references prevent deletion without library changes");
            referenceCanvas["Canvas"]!["Nodes"]![0]!["References"] = new System.Text.Json.Nodes.JsonArray();
            File.WriteAllText(referenceCanvasPath, referenceCanvas.ToJsonString());
            var disposableVariant = (await Json(admin, "/api/web/assets")).GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == entityKey).GetProperty("variants")[1].GetProperty("id").GetGuid();
            var primaryVariantKey = variantKey; variantKey = disposableVariant;
            await EditEntity("deleteVariant"); variantKey = primaryVariantKey;
            Assert((await Json(admin, "/api/web/assets")).GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == entityKey).GetProperty("variants").GetArrayLength() == 1, "unreferenced variant deletion persists");
            // 网页端「项目资源库」面板列的就是**项目库**（/api/web/assets 直接读 entities.json），
            // 所以在库上新建的设定必须能直接挂到节点上。从前这里是「列表里选得到、一选必然
            // 404 CANVAS_ENTITY_NOT_FOUND」——挂载只认画布里的快照，而新建的库实体还不在画布里。
            // 现在按引用把库实体补成画布快照（与桌面端 ProjectEntityScope.MergeInto 的注入规则同一条），
            // 这条就是那件事的回归。
            var bindingScene = await Json(admin, "/api/web/scene");
            var bindingNode = await Json(admin, "/api/web/records", JsonSerializer.Serialize(new { baseRevision = bindingScene.GetProperty("revision").GetInt64(), recordType = "general", title = "引用挂载" }));
            var bindingNodeId = bindingNode.GetProperty("nodeId").GetGuid();
            var bindingBefore = await Json(admin, "/api/web/scene");
            await Json(admin, $"/api/web/records/{bindingNodeId}/references",
                JsonSerializer.Serialize(new { baseRevision = bindingBefore.GetProperty("revision").GetInt64(), entityId = entityKey, variantId = variantKey }));
            var bindingAfter = await Json(admin, "/api/web/scene");
            var bindingRecord = bindingAfter.GetProperty("records").EnumerateArray().Single(r => r.GetProperty("recordId").GetString() == bindingNodeId.ToString());
            Assert(bindingRecord.GetProperty("record").GetProperty("references").EnumerateArray()
                    .Any(r => r.GetProperty("entityId").GetString() == entityKey.ToString()),
                "library-only entity binds through the API: " + bindingRecord);
            Assert(File.ReadAllText(canvasPath).Contains(entityKey.ToString(), StringComparison.Ordinal),
                "the bind writes a managed snapshot of that entity into the canvas");
            Console.WriteLine("Entity HTTP regression passed: permissions, CAS, descriptions, shared commit/rollback, protected versions, media bytes/rename/detach and historical media retention");
            // ---- 聊天技能的任意参数与实体引用 ----
            mode = "success";
            // ① 参数校验：六种类型都在，每种错误都有自己的稳定错误码。
            //    声明与校验是同一份实现，这里直接钉住它；技能侧的读取另外走真实调用链（见下）。
            YEEYEEYEE.Web.ChatParameterSpec Param(string name, YEEYEEYEE.Web.ChatParameterKind kind, bool required = false, string[]? options = null, double? min = null, double? max = null) =>
                new(name, kind, required, name, options ?? Array.Empty<string>(), false, min, max);
            var declaredKinds = new List<YEEYEEYEE.Web.ChatParameterSpec> {
                Param("s", YEEYEEYEE.Web.ChatParameterKind.String), Param("n", YEEYEEYEE.Web.ChatParameterKind.Number, min: 1, max: 10),
                Param("b", YEEYEEYEE.Web.ChatParameterKind.Boolean), Param("e", YEEYEEYEE.Web.ChatParameterKind.Enum, options: new[] { "a", "b" }),
                Param("l", YEEYEEYEE.Web.ChatParameterKind.Array), Param("o", YEEYEEYEE.Web.ChatParameterKind.Object),
                Param("must", YEEYEEYEE.Web.ChatParameterKind.String, required: true)
            };
            bool Check(object parameters, out string? code)
            {
                var supplied = JsonSerializer.SerializeToElement(parameters).EnumerateObject()
                    .ToDictionary(pair => pair.Name, pair => pair.Value.Clone(), StringComparer.Ordinal);
                WebChatSkillParameters.TryValidate(declaredKinds, supplied, Array.Empty<string>(), out _, out code, out _, out _);
                return code is null;
            }
            Assert(Check(new { s = "文本", n = 10, b = true, e = "a", l = new[] { "x", "y" }, o = new { key = "值", count = 2, flag = false }, must = "值" }, out var kindsCode) && kindsCode is null,
                "six declared parameter kinds (string/number/boolean/enum/array/object) all validate");
            Assert(!Check(new { must = "值", n = 1.5 }, out var decimalCode) && decimalCode == "SKILL_PARAM_TYPE", "decimal numbers are rejected instead of silently rounded");
            Assert(!Check(new { must = "值", n = 11 }, out var rangeCode) && rangeCode == "SKILL_PARAM_TYPE", "out-of-range numbers are rejected");
            Assert(!Check(new { must = "值", b = "true" }, out var booleanCode) && booleanCode == "SKILL_PARAM_TYPE", "a string is not accepted as a boolean");
            Assert(!Check(new { must = "值", e = "c" }, out var enumCode) && enumCode == "SKILL_PARAM_INVALID", "enum values outside the declaration are rejected");
            Assert(!Check(new { must = "值", l = new object[] { 1 } }, out var arrayCode) && arrayCode == "SKILL_PARAM_INVALID", "non-string array items are rejected");
            Assert(!Check(new { must = "值", o = new { nested = new { deep = 1 } } }, out var objectCode) && objectCode == "SKILL_PARAM_INVALID", "nested object values are rejected");
            Assert(!Check(new { must = "值", unknownKey = 1 }, out var unknownCode) && unknownCode == "SKILL_PARAM_UNKNOWN", "unknown parameter names are rejected instead of dropped");
            Assert(!Check(new { must = "值", prompt = "x" }, out var protocolCode) && protocolCode == "SKILL_INPUT_UNSUPPORTED", "protocol-known parameters this skill does not take are rejected explicitly");
            Assert(!Check(new { }, out var requiredCode) && requiredCode == "SKILL_PARAM_REQUIRED", "missing required parameters are rejected");
            // 声明决定了界面能摆哪些控件：内置技能必须**真的**声明出可编辑的字符串/数字/枚举/数组，
            // 否则浏览器上就没有对应的控件（布尔与对象当前没有内置技能声明，这一点如实记录在文档里）。
            string[] Editable(string skill) => WebChatSkillParameters.Declare(skill)
                .Where(spec => spec.Editable).Select(spec => spec.Name + ":" + spec.Kind.ToString().ToLowerInvariant()).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert(Editable("comfyui.text-to-image").SequenceEqual(new[] { "aspectRatio:enum", "negativePrompt:string", "referenceAttachmentIds:array", "seed:number" }),
                "local image skill declares editable enum/string/array/number parameters: " + string.Join("、", Editable("comfyui.text-to-image")));
            Assert(Editable("web.text-to-image").Contains("referenceAttachmentIds:array") && !Editable("web.text-to-image").Contains("seed:number"),
                "web image skill declares the array reference parameter but no seed (its provider does not read one)");
            Assert(Editable("web.text-to-video").Contains("seconds:number") && Editable("web.text-to-video").Contains("width:number")
                && Editable("web.text-to-video").Contains("height:number"), "video skill declares editable numeric parameters");
            Assert(new[] { 1, 2, 15 }.Select(WebChatSkillParameters.VideoFramesForSeconds).SequenceEqual(new[] { 25, 49, 361 }),
                "video seconds fold to 1+8k frames exactly like the node menu");

            // ② 端到端：模型与前端提交的声明参数经聊天技能入口落到**执行层输入**（执行器读的就是这些键）。
            //    提交只按界面显示的那一整套**替换**：清空/移除必须真的不再生效，而不是退回模型给的值。
            async Task<(string Session, string Message)> ImageProposal(object[] entityReferences, string? negativePrompt = null, string[]? referenceAttachmentIds = null)
            {
                var action = new Dictionary<string, object?>
                {
                    ["kind"] = "invoke_skill", ["target"] = "comfyui.text-to-image",
                    ["content"] = "参数与引用用例", ["entityReferences"] = entityReferences
                };
                if (negativePrompt is not null) action["parameters"] = new { negativePrompt };
                if (referenceAttachmentIds is not null) action["referenceAttachmentIds"] = referenceAttachmentIds;
                modelText = "```json\n" + JsonSerializer.Serialize(new { actions = new[] { action } }) + "\n```";
                var sid = (await Json(admin, "/api/web/chat/sessions", "{}")).GetProperty("id").GetString()!;
                var session = await Send(sid, "{\"text\":\"生成图片\",\"mode\":\"readonly\"}");
                return (sid, session.GetProperty("messages")[1].GetProperty("id").GetString()!);
            }
            async Task<string> CardError(string sessionId) =>
                (await Json(admin, "/api/web/chat/sessions/" + sessionId)).GetProperty("messages")[1].GetProperty("skillError").GetString() ?? "";
            string InvokePath(string sessionId, string messageId) => $"/api/web/chat/sessions/{sessionId}/skills/{messageId}/invoke";
            using var paramJobs = new YEEYEEYEE.Host.SqliteJobStore(Path.Combine(directory, "jobs.db"));
            IReadOnlyDictionary<string, JsonElement> JobInputs(Guid jobId)
            {
                // 发起接口现在提交后立即返回，任务由后台线程登记落库；CI 上这段窗口更明显，
                // 所以等它真正出现在任务库里再读输入，而不是假设响应回来就已经写完。
                for (var attempt = 0; attempt < 200; attempt++)
                {
                    var stored = paramJobs.LoadAll().FirstOrDefault(job => job.JobId == jobId);
                    if (stored is not null) return stored.Inputs;
                    Thread.Sleep(10);
                }
                throw new Exception("Job never reached the store: " + jobId);
            }

            var (unknownSession, unknownMessage) = await ImageProposal(Array.Empty<object>());
            await Json(admin, InvokePath(unknownSession, unknownMessage), "{\"aspectRatio\":\"1:1\",\"parameters\":{\"unknownKey\":1}}", 400);
            Assert((await CardError(unknownSession)).Contains("SKILL_PARAM_UNKNOWN"), "chat rejects an unknown parameter with a stable code");
            var (parameterTypeSession, parameterTypeMessage) = await ImageProposal(Array.Empty<object>());
            await Json(admin, InvokePath(parameterTypeSession, parameterTypeMessage), "{\"aspectRatio\":\"1:1\",\"parameters\":{\"seed\":\"42\"}}", 400);
            Assert((await CardError(parameterTypeSession)).Contains("SKILL_PARAM_TYPE"), "chat rejects a wrongly typed parameter with a stable code");
            var (unsupportedSession, unsupportedMessage) = await ImageProposal(Array.Empty<object>());
            await Json(admin, InvokePath(unsupportedSession, unsupportedMessage), "{\"aspectRatio\":\"1:1\",\"parameters\":{\"width\":512}}", 400);
            Assert((await CardError(unsupportedSession)).Contains("SKILL_INPUT_UNSUPPORTED"), "chat rejects a parameter this skill does not take instead of ignoring it");
            // 界面没有控件的参数（模型自己编的 width）改不动 → 只能整条挡住。
            Assert((await Json(admin, "/api/web/chat/sessions/" + unsupportedSession)).GetProperty("messages")[1].GetProperty("skillState").GetString() == "blocked",
                "a parameter error without a card control blocks the proposal");
            // 界面上有控件的参数（seed）改得动 → 留在可重试状态，改正后重新提交要能过。
            var (retryableSession, retryableMessage) = await ImageProposal(Array.Empty<object>());
            await Json(admin, InvokePath(retryableSession, retryableMessage), "{\"aspectRatio\":\"1:1\",\"parameters\":{\"seed\":\"不是数字\"}}", 400);
            var retryableCard = (await Json(admin, "/api/web/chat/sessions/" + retryableSession)).GetProperty("messages")[1];
            Assert(retryableCard.GetProperty("skillState").GetString() == "failed" && retryableCard.GetProperty("skillError").GetString()!.Contains("SKILL_PARAM_TYPE"),
                "an editable parameter error stays retryable instead of blocking the card");
            var correctedJob = await Json(admin, InvokePath(retryableSession, retryableMessage), "{\"aspectRatio\":\"1:1\",\"parameters\":{\"seed\":0}}");
            Assert(JobInputs(correctedJob.GetProperty("jobId").GetGuid()).TryGetValue("seed", out var correctedSeed) && correctedSeed.GetInt32() == 0,
                "a corrected value can be resubmitted from the same card");
            var (parameterSession, parameterMessage) = await ImageProposal(Array.Empty<object>());
            var parameterJob = await Json(admin, InvokePath(parameterSession, parameterMessage), "{\"aspectRatio\":\"1:1\",\"parameters\":{\"seed\":42,\"negativePrompt\":\"模糊\"}}");
            var parameterInputs = JobInputs(parameterJob.GetProperty("jobId").GetGuid());
            Assert(parameterInputs.TryGetValue("seed", out var seedValue) && seedValue.GetInt32() == 42
                && parameterInputs.TryGetValue("negativePrompt", out var negativeValue) && negativeValue.GetString() == "模糊"
                && parameterInputs.TryGetValue("aspectRatio", out var ratioValue) && ratioValue.GetString() == "1:1",
                "declared chat parameters reach the execution-layer inputs");
            // 界面显示什么就提交什么（替换，不是合并）：改过的按用户的、清空的真的不再生效。
            var (editedSession, editedMessage) = await ImageProposal(Array.Empty<object>(), negativePrompt: "模型给的");
            var editedJob = await Json(admin, InvokePath(editedSession, editedMessage),
                JsonSerializer.Serialize(new { aspectRatio = "1:1", parameters = new { negativePrompt = "用户改的" }, entityReferences = Array.Empty<object>() }));
            Assert(JobInputs(editedJob.GetProperty("jobId").GetGuid()).TryGetValue("negativePrompt", out var editedValue) && editedValue.GetString() == "用户改的",
                "an edited parameter replaces the model value");
            var (clearedSession, clearedMessage) = await ImageProposal(Array.Empty<object>(), negativePrompt: "模型给的");
            var clearedJob = await Json(admin, InvokePath(clearedSession, clearedMessage),
                JsonSerializer.Serialize(new { aspectRatio = "1:1", parameters = new { }, entityReferences = Array.Empty<object>() }));
            Assert(!JobInputs(clearedJob.GetProperty("jobId").GetGuid()).ContainsKey("negativePrompt"),
                "clearing a field removes the model value instead of merging it back");
            var (zeroSession, zeroMessage) = await ImageProposal(Array.Empty<object>());
            var zeroJob = await Json(admin, InvokePath(zeroSession, zeroMessage),
                JsonSerializer.Serialize(new { aspectRatio = "1:1", parameters = new { seed = 0 }, entityReferences = Array.Empty<object>() }));
            Assert(JobInputs(zeroJob.GetProperty("jobId").GetGuid()).TryGetValue("seed", out var zeroSeed) && zeroSeed.GetInt32() == 0,
                "zero is submitted as a real value, not treated as unset");
            // 参考图：模型选了它、用户在卡片上全部取消 → 这次就不再带参考图（清空平铺字段与参数两处）。
            var referenceInputSession = (await Json(admin, "/api/web/chat/sessions", "{}")).GetProperty("id").GetString()!;
            using (var referenceContent = new ByteArrayContent(imageBytes))
            {
                referenceContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                using var uploaded = await admin.PostAsync($"/api/web/chat/sessions/{referenceInputSession}/attachments?name=ref.png", referenceContent);
                var uploadedId = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
                modelText = "回答";
                await Send(referenceInputSession, JsonSerializer.Serialize(new { text = "参考图在这", attachmentIds = new[] { uploadedId } }));
                modelText = "```json\n" + JsonSerializer.Serialize(new { actions = new[] { new { kind = "invoke_skill", target = "comfyui.text-to-image", content = "参考图替换用例", referenceAttachmentIds = new[] { uploadedId } } } }) + "\n```";
                var referenceCard = await Send(referenceInputSession, "{\"text\":\"用它出图\",\"mode\":\"readonly\"}");
                var referenceMessage = referenceCard.GetProperty("messages")[3].GetProperty("id").GetString();
                var droppedJob = await Json(admin, InvokePath(referenceInputSession, referenceMessage),
                    JsonSerializer.Serialize(new { aspectRatio = "1:1", parameters = new { referenceAttachmentIds = Array.Empty<string>() }, entityReferences = Array.Empty<object>() }));
                var droppedInputs = JobInputs(droppedJob.GetProperty("jobId").GetGuid());
                Assert(!droppedInputs.ContainsKey("referenceAttachmentIds")
                    && (!droppedInputs.TryGetValue("referenceImages", out var keptImages) || keptImages.GetArrayLength() == 0),
                    "clearing the reference images really removes the model's choice");
            }

            // ③ 实体引用：人物 / 场景 / 道具 / 项目节点——保存类型 + ID + 显示名 + 版本，并传给执行层。
            await Json(admin, "/api/web/entities/manage", JsonSerializer.Serialize(new { operation = "createEntity", name = "引用场景", kind = "Scene", baseRevision = (await Json(admin, "/api/web/assets")).GetProperty("revision").GetInt64() }));
            await Json(admin, "/api/web/entities/manage", JsonSerializer.Serialize(new { operation = "createEntity", name = "引用道具", kind = "Prop", baseRevision = (await Json(admin, "/api/web/assets")).GetProperty("revision").GetInt64() }));
            var referenceAssets = await Json(admin, "/api/web/assets");
            var sceneEntity = referenceAssets.GetProperty("entities").EnumerateArray().Single(entity => entity.GetProperty("name").GetString() == "引用场景");
            var propEntity = referenceAssets.GetProperty("entities").EnumerateArray().Single(entity => entity.GetProperty("name").GetString() == "引用道具");
            var characterEntity = referenceAssets.GetProperty("entities").EnumerateArray().Single(entity => entity.GetProperty("id").GetGuid() == entityKey);
            var characterVariant = characterEntity.GetProperty("variants")[0];
            var characterVersions = characterVariant.GetProperty("versions");
            var lockedVersion = characterVersions[characterVersions.GetArrayLength() - 1];
            var referenceEntries = new object[] {
                new { kind = "character", id = entityKey.ToString(), variantId = variantKey.ToString(), versionId = lockedVersion.GetProperty("id").GetString() },
                new { kind = "scene", id = sceneEntity.GetProperty("id").GetString() },
                new { kind = "prop", id = propEntity.GetProperty("id").GetString() },
                new { kind = "node", id = bindingNodeId.ToString() }
            };
            var (entitySession, entityMessage) = await ImageProposal(referenceEntries);
            // 前端提交的是界面显示的那一整套引用（与服务端已解析的一致）：替换之后仍是这四条。
            var entityJob = await Json(admin, InvokePath(entitySession, entityMessage),
                JsonSerializer.Serialize(new { aspectRatio = "1:1", parameters = new { referenceAttachmentIds = Array.Empty<string>() }, entityReferences = referenceEntries }));
            var echoedReferences = (await Json(admin, "/api/web/chat/sessions/" + entitySession))
                .GetProperty("messages")[1].GetProperty("skillActions")[0].GetProperty("entityReferences");
            Assert(echoedReferences.GetArrayLength() == 4, "all four reference kinds are stored on the chat message");
            Assert(echoedReferences.EnumerateArray().Any(reference => reference.GetProperty("kind").GetString() == "character"
                    && reference.GetProperty("name").GetString() == "HTTP角色"
                    && reference.GetProperty("variantName").GetString() == "默认"
                    && reference.GetProperty("version").GetString() == "v" + lockedVersion.GetProperty("number").GetInt32()),
                "character reference keeps type, id, server-resolved display name and version");
            Assert(echoedReferences.EnumerateArray().Any(reference => reference.GetProperty("kind").GetString() == "scene" && reference.GetProperty("name").GetString() == "引用场景")
                && echoedReferences.EnumerateArray().Any(reference => reference.GetProperty("kind").GetString() == "prop" && reference.GetProperty("name").GetString() == "引用道具")
                && echoedReferences.EnumerateArray().Any(reference => reference.GetProperty("kind").GetString() == "node" && reference.GetProperty("name").GetString() == "引用挂载"),
                "scene, prop and project-node references resolve to authoritative names");
            Assert(JobInputs(entityJob.GetProperty("jobId").GetGuid()).TryGetValue("entityReferences", out var storedReferences) && storedReferences.GetArrayLength() == 4,
                "resolved entity references reach the execution-layer inputs");
            // 在卡片上移除引用：提交的就是剩下的那几条（替换，不是把旧的合并回来）。
            var (trimSession, trimMessage) = await ImageProposal(referenceEntries);
            var trimJob = await Json(admin, InvokePath(trimSession, trimMessage),
                JsonSerializer.Serialize(new { aspectRatio = "1:1", entityReferences = new[] { new { kind = "node", id = bindingNodeId.ToString() } } }));
            Assert(JobInputs(trimJob.GetProperty("jobId").GetGuid()).TryGetValue("entityReferences", out var trimmedReferences)
                && trimmedReferences.GetArrayLength() == 1 && trimmedReferences[0].GetProperty("name").GetString() == "引用挂载",
                "removing references in the card replaces the set instead of merging the old ones back");
            var (missingSession, missingMessage) = await ImageProposal(new object[] { new { kind = "character", id = Guid.NewGuid().ToString() } });
            await Json(admin, InvokePath(missingSession, missingMessage), "{\"aspectRatio\":\"1:1\"}", 400);
            Assert((await CardError(missingSession)).Contains("SKILL_ENTITY_INVALID"), "an unknown entity reference is rejected explicitly");
            var (mismatchSession, mismatchMessage) = await ImageProposal(new object[] { new { kind = "prop", id = sceneEntity.GetProperty("id").GetString() } });
            await Json(admin, InvokePath(mismatchSession, mismatchMessage), "{\"aspectRatio\":\"1:1\"}", 400);
            Assert((await CardError(mismatchSession)).Contains("SKILL_ENTITY_INVALID"), "an entity kind mismatch is rejected instead of guessed");
            var (shapeSession, shapeMessage) = await ImageProposal(new object[] { new { kind = "character" } });
            await Json(admin, InvokePath(shapeSession, shapeMessage), "{\"aspectRatio\":\"1:1\"}", 400);
            Assert((await CardError(shapeSession)).Contains("SKILL_ENTITY_INVALID"), "a reference without an id is rejected instead of silently dropped");
            Console.WriteLine("Chat parameter and entity reference regression passed: six declared kinds with stable error codes, execution-layer inputs and four reference kinds echoed back");
            modelText = "回答";
            await using var attachmentMock = new MockComfyUiServer();
            await attachmentMock.StartAsync(); attachmentBackend = attachmentMock.BaseAddress;
            Stop(); await Start();
            async Task<JsonElement> WaitAttachment(Guid jobId, Func<string, bool> finished)
            {
                JsonElement job = default;
                for (var i = 0; i < 100; i++)
                {
                    job = await Json(admin, "/api/web/jobs/" + jobId);
                    if (finished(job.GetProperty("attachmentState").GetString()!)) return job;
                    await Task.Delay(100);
                }
                throw new Exception("Attachment timeout: " + job);
            }
            var imageConfig = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))!;
            imageConfig["ImageEndpoint"] = $"http://127.0.0.1:{modelPort}/v1";
            imageConfig["ImageModel"] = "grok-image-2.0(池4)";
            imageConfig["ImageMaxReferenceImages"] = 0;
            File.WriteAllText(configPath, imageConfig.ToJsonString());
            var published = await Json(admin, "/api/web/skills");
            Assert(published.GetProperty("skills").EnumerateArray().Any(s => s.GetProperty("id").GetString() == "web.text-to-image"), "independent image API publishes skill");
            mode = "protocol-example";
            var protocolSession = (await Json(admin, "/api/web/chat/sessions", "{}")).GetProperty("id").GetString()!;
            var protocolDone = await Send(protocolSession, "{\"text\":\"生成蓝天白云插画，无参考，1:1\",\"mode\":\"readonly\"}");
            mode = "success";
            var protocolReply = protocolDone.GetProperty("messages")[1];
            Assert(protocolReply.GetProperty("state").GetString() == "completed" &&
                protocolReply.GetProperty("skillActions")[0].GetProperty("target").GetString() == "web.text-to-image" &&
                protocolReply.GetProperty("skillActions")[0].GetProperty("aspectRatio").GetString() == "1:1" &&
                protocolReply.GetProperty("skillActions")[0].GetProperty("unsupportedSkillFields").GetArrayLength() == 0 &&
                protocolReply.GetProperty("skillState").GetString() == "preview" &&
                protocolDone.GetProperty("jobIds").GetArrayLength() == 0,
                "actual request protocol example parses and waits for ratio without submitting image task");
            await Json(admin, "/api/web/skills/web.text-to-image/invoke", "{\"prompt\":\"offline image\",\"idempotencyKey\":\"no-ratio\"}", 400);
            int requestsBeforeReference; lock (captured) requestsBeforeReference = captured.Count;
            await Json(admin, "/api/web/skills/web.text-to-image/invoke", JsonSerializer.Serialize(new { prompt = "reference", aspectRatio = "1:1", idempotencyKey = "unsupported-image-ref", chatSessionId = attachmentSession, referenceAttachmentIds = new[] { imageAttachment } }), 400);
            lock (captured) Assert(captured.Count == requestsBeforeReference, "unsupported reference blocked before provider request");
            var imageJob = await Json(admin, "/api/web/skills/web.text-to-image/invoke", "{\"prompt\":\"offline image\",\"aspectRatio\":\"9:16\",\"idempotencyKey\":\"api-image\"}");
            for (var i = 0; i < 100 && imageJob.GetProperty("state").GetString() is "Queued" or "Running"; i++)
            { await Task.Delay(50); imageJob = await Json(admin, "/api/web/jobs/" + imageJob.GetProperty("jobId").GetString()); }
            Assert(imageJob.GetProperty("state").GetString() == "Succeeded" && imageJob.GetProperty("outputs")[0].GetProperty("role").GetString() == "image", "API image uses existing job assets");
            lock (captured) Assert(captured.Any(p => p.TryGetProperty("size", out var size) && size.GetString() == "864x1536"), "chosen portrait ratio reaches shared image provider");
            using (var imageOutput = await admin.GetAsync("/api/web/jobs/" + imageJob.GetProperty("jobId").GetString() + "/outputs/0"))
                Assert(imageOutput.IsSuccessStatusCode, "API image output available through owned job route");
            var unlockedCanvas = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(canvasPath))!;
            unlockedCanvas["Canvas"]!["Nodes"]!.AsArray().Single(n => n!["Id"]!.GetValue<Guid>() == nodeId)!["IsLocked"] = false;
            File.WriteAllText(canvasPath, unlockedCanvas.ToJsonString());
            foreach (var generationSkill in new[] { "web.text-to-video", "web.text-to-image" })
            foreach (var canvasMode in new[] { "readonly", "approval", "auto" })
            {
                var generatedBefore = File.ReadAllBytes(canvasPath);
                var generated = await Proposal(canvasMode, SkillOutput(generationSkill));
                var generatedSession = await Json(admin, "/api/web/chat/sessions/" + generated.Session);
                Assert(generatedSession.GetProperty("jobIds").GetArrayLength() == 1, canvasMode + " conversation submits generation");
                var generatedId = generatedSession.GetProperty("jobIds")[0].GetGuid();
                Task<JsonElement>? concurrentReply = null;
                if (canvasMode == "readonly")
                {
                    mode = "slow"; modelText = "继续沟通";
                    concurrentReply = Send(generated.Session, "{\"text\":\"生成期间继续聊\",\"mode\":\"readonly\"}");
                }
                JsonElement generatedJob = default;
                for (var i = 0; i < 100; i++)
                {
                    generatedJob = await Json(admin, "/api/web/jobs/" + generatedId);
                    if (generatedJob.GetProperty("state").GetString() == "Succeeded") break;
                    await Task.Delay(100);
                }
                Assert(generatedJob.GetProperty("state").GetString() == "Succeeded" && generatedJob.GetProperty("outputs").GetArrayLength() == 1, canvasMode + " actual successful output");
                Assert(generatedJob.GetProperty("targetNodeId").GetGuid() == nodeId && generatedJob.GetProperty("attachmentState").GetString() == "none", canvasMode + " no background attachment target");
                JsonElement completedMessage = default;
                for (var i = 0; i < 100; i++)
                {
                    generatedSession = await Json(admin, "/api/web/chat/sessions/" + generated.Session);
                    completedMessage = generatedSession.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("id").GetString() == generated.Message);
                    if (completedMessage.GetProperty("skillState").GetString() == "completed") break;
                    await Task.Delay(100);
                }
                if (concurrentReply is not null)
                {
                    await concurrentReply; mode = "success";
                    generatedSession = await Json(admin, "/api/web/chat/sessions/" + generated.Session);
                    Assert(generatedSession.GetProperty("messages").GetArrayLength() == 4 && generatedSession.GetProperty("messages")[3].GetProperty("content").GetString() == "继续沟通", "background completion preserves concurrent streaming messages");
                }
                Assert(completedMessage.GetProperty("skillState").GetString() == "completed" && completedMessage.GetProperty("actions").EnumerateArray().Any(a => a.GetProperty("kind").GetString() == "attach_asset"), canvasMode + " successful output appends real canvas action");
                if (canvasMode == "readonly")
                {
                    Assert(generatedBefore.SequenceEqual(File.ReadAllBytes(canvasPath)), "readonly output never writes canvas");
                    await Json(admin, ActionPath(generated, "commit"), "{}", 403);
                }
                else
                {
                    if (canvasMode == "approval")
                    {
                        Assert(generatedBefore.SequenceEqual(File.ReadAllBytes(canvasPath)), "approval output waits for existing canvas confirmation");
                        await Json(admin, ActionPath(generated, "preview"), "{}");
                        await Json(admin, ActionPath(generated, "commit"), "{}");
                    }
                    else Assert(completedMessage.GetProperty("actionState").GetString() == "committed", "auto output uses shared commit without confirmation");
                    var persisted = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(canvasPath))!;
                    Assert(persisted["Canvas"]!["Nodes"]!.AsArray().Single(n => n!["Id"]!.GetValue<Guid>() == nodeId)!["Attachments"]!.AsArray().Any(a => a!["Reference"]!.GetValue<string>() == generatedJob.GetProperty("outputs")[0].GetProperty("ref").GetString()), canvasMode + " generated media persists on selected node");
                    await Json(admin, ActionPath(generated, "undo"), "{}");
                    var undoneCanvas = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(canvasPath))!;
                    Assert(!undoneCanvas["Canvas"]!["Nodes"]!.AsArray().Single(n => n!["Id"]!.GetValue<Guid>() == nodeId)!["Attachments"]!.AsArray().Any(a => a!["Reference"]!.GetValue<string>() == generatedJob.GetProperty("outputs")[0].GetProperty("ref").GetString()), canvasMode + " attachment undo removes generated media");
                    // 保存会补齐旧画布缺省字段；按同一持久化模型比较所有内容，仅允许修订号递增。
                    var originalState = JsonSerializer.Deserialize<YEEYEEYEE.Desktop.RecentCanvasState>(generatedBefore)!;
                    var undoneState = JsonSerializer.Deserialize<YEEYEEYEE.Desktop.RecentCanvasState>(File.ReadAllBytes(canvasPath))!;
                    var originalCanvas = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(originalState with { Revision = undoneState.Revision }))!;
                    var restoredCanvas = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(undoneState))!;
                    Assert(System.Text.Json.Nodes.JsonNode.DeepEquals(originalCanvas, restoredCanvas), canvasMode + " undo restores all canvas content with a new revision");
                    var undoneBytes = File.ReadAllBytes(canvasPath);
                    await Task.Delay(1200);
                    Assert(undoneBytes.SequenceEqual(File.ReadAllBytes(canvasPath)), "completed worker does not reattach undone media");
                }
                modelText = "回答";
                await Send(generated.Session, "{\"text\":\"生成结果是什么\"}");
                Assert(captured.Last().ToString().Contains("Succeeded") && captured.Last().ToString().Contains("asset://"), "successful output resource enters conversation context");
            }
            var referenceState = JsonSerializer.Deserialize<YEEYEEYEE.Desktop.RecentCanvasState>(File.ReadAllBytes(canvasPath))!;
            var referenceEntities = new[] { YEEYEEYEE.Desktop.EntityKind.Character, YEEYEEYEE.Desktop.EntityKind.Prop, YEEYEEYEE.Desktop.EntityKind.Scene }
                .Select(kind => new YEEYEEYEE.Desktop.WorkflowEntity { Name = "Reference " + kind, Kind = kind, ManagedByProject = true }).ToArray();
            foreach (var entity in referenceEntities) { entity.Variants.Clear(); entity.Variants.Add(new YEEYEEYEE.Desktop.WorkflowEntityVariant()); }
            var referenceShot = new YEEYEEYEE.Desktop.WorkflowNode { Category = YEEYEEYEE.Desktop.NodeCategory.Storyboard, Title = "Required three references",
                AssetRequirements = new() { Character = YEEYEEYEE.Desktop.AssetNeed.Required, Prop = YEEYEEYEE.Desktop.AssetNeed.Required, Scene = YEEYEEYEE.Desktop.AssetNeed.Required } };
            foreach (var entity in referenceEntities) referenceShot.References.Add(new() { EntityId = entity.Id, VariantId = entity.Variants[0].Id });
            referenceState.Canvas.Entities.AddRange(referenceEntities); referenceState.Canvas.Nodes.Add(referenceShot);
            var referenceLibrary = JsonSerializer.Deserialize<YEEYEEYEE.Desktop.ProjectEntityFile>(File.ReadAllBytes(entitiesPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            referenceLibrary.Entities.AddRange(referenceEntities);
            void SaveReferences() { File.WriteAllText(entitiesPath, JsonSerializer.Serialize(referenceLibrary, new JsonSerializerOptions(JsonSerializerDefaults.Web))); File.WriteAllText(canvasPath, JsonSerializer.Serialize(referenceState)); }
            SaveReferences();
            var jobCountBefore = (await Json(admin, "/api/web/jobs")).GetProperty("jobs").GetArrayLength();
            string ReferenceRequest(string key) => JsonSerializer.Serialize(new { prompt = "three references", aspectRatio = "1:1", idempotencyKey = key, targetNodeId = referenceShot.Id });
            await Json(admin, "/api/web/skills/web.text-to-image/invoke", ReferenceRequest("missing-entity-images"), 409);
            Assert((await Json(admin, "/api/web/jobs")).GetProperty("jobs").GetArrayLength() == jobCountBefore, "missing entity images creates no job");
            foreach (var entity in referenceEntities)
            {
                entity.Variants[0].Attachments.Add(new() { Kind = YEEYEEYEE.Desktop.AttachmentKind.Image, Reference = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=" });
                entity.Variants[0].EnsureInitialVersion();
            }
            SaveReferences();
            await Json(admin, "/api/web/skills/web.text-to-image/invoke", ReferenceRequest("zero-entity-capacity"), 409);
            Assert((await Json(admin, "/api/web/jobs")).GetProperty("jobs").GetArrayLength() == jobCountBefore, "zero capacity creates no job");
            imageConfig["ImageMaxReferenceImages"] = 3; imageConfig["ImageModel"] = "mock-reference-image"; File.WriteAllText(configPath, imageConfig.ToJsonString());
            var threeJob = await Json(admin, "/api/web/skills/web.text-to-image/invoke", ReferenceRequest("all-three-entity-images"));
            for (var i = 0; i < 100 && threeJob.GetProperty("state").GetString() is "Queued" or "Running"; i++)
            { await Task.Delay(50); threeJob = await Json(admin, "/api/web/jobs/" + threeJob.GetProperty("jobId").GetString()); }
            Assert(threeJob.GetProperty("state").GetString() == "Succeeded", "three references generates successfully");
            lock (referenceCounts) Assert(referenceCounts.Last() == 3, "actual image HTTP request contains all three required references");
            Console.WriteLine("Entity reference generation regression passed: missing images and zero capacity create no jobs, all three images reach actual provider request");
            await WaitAttachment(threeJob.GetProperty("jobId").GetGuid(), state => state == "attached");
            var outputFixture = JsonSerializer.Deserialize<YEEYEEYEE.Desktop.RecentCanvasState>(File.ReadAllBytes(canvasPath))!;
            var outputNode = outputFixture.Canvas.Nodes.Single(n => n.Id == nodeId);
            outputNode.Category = YEEYEEYEE.Desktop.NodeCategory.Prop;
            outputNode.References.Clear(); outputNode.References.Add(new() { EntityId = referenceEntities[1].Id, VariantId = referenceEntities[1].Variants[0].Id });
            File.WriteAllText(canvasPath, JsonSerializer.Serialize(outputFixture));
            foreach (var outputMode in new[] { "readonly", "approval", "auto" })
            {
                var outputLibraryBefore = File.ReadAllBytes(entitiesPath);
                var versionBefore = JsonSerializer.Deserialize<YEEYEEYEE.Desktop.ProjectEntityFile>(outputLibraryBefore, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Entities.Single(e => e.Id == referenceEntities[1].Id).Variants[0].CurrentVersion;
                var generated = await Proposal(outputMode, SkillOutput("web.text-to-image"));
                JsonElement outputMessage = default;
                for (var i = 0; i < 100; i++)
                {
                    var outputSession = await Json(admin, "/api/web/chat/sessions/" + generated.Session);
                    outputMessage = outputSession.GetProperty("messages")[1];
                    if (outputMessage.GetProperty("skillState").GetString() == "completed") break;
                    await Task.Delay(100);
                }
                Assert(outputMessage.GetProperty("skillState").GetString() == "completed", "entity output finishes " + outputMode);
                if (outputMode == "readonly") Assert(outputLibraryBefore.SequenceEqual(File.ReadAllBytes(entitiesPath)), "readonly leaves entity library unchanged");
                else
                {
                    if (outputMode == "approval")
                    {
                        Assert(outputLibraryBefore.SequenceEqual(File.ReadAllBytes(entitiesPath)), "approval waits before entity commit");
                        // 产物已回挂（附件动作是服务端追加的）：先按最新动作预检，再提交。
                        await Json(admin, ActionPath(generated, "preview"), "{}");
                        await Json(admin, ActionPath(generated, "commit"), "{}");
                    }
                    var committedVariant = JsonSerializer.Deserialize<YEEYEEYEE.Desktop.ProjectEntityFile>(File.ReadAllBytes(entitiesPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Entities.Single(e => e.Id == referenceEntities[1].Id).Variants[0];
                    Assert(committedVariant.CurrentVersion == versionBefore + 1 && committedVariant.Attachments[0].Reference.StartsWith("asset://"), "entity image commits authoritative variant version " + outputMode);
                    if (outputMode == "approval")
                    {
                        var referencedCanvas = new YEEYEEYEE.Desktop.RecentCanvasState();
                        var referencedNode = new YEEYEEYEE.Desktop.WorkflowNode();
                        referencedNode.References.Add(new() { EntityId = referenceEntities[1].Id, VariantId = committedVariant.Id, VariantVersionId = committedVariant.Versions.Single(v => v.Number == committedVariant.CurrentVersion).Id });
                        referencedCanvas.Canvas.Nodes.Add(referencedNode);
                        var referencedPath = Path.Combine(Path.GetDirectoryName(canvasPath)!, "undo-reference-fixture.json");
                        File.WriteAllText(referencedPath, JsonSerializer.Serialize(referencedCanvas));
                        var committedBytes = File.ReadAllBytes(entitiesPath);
                        await Json(admin, ActionPath(generated, "undo"), "{}", status: 409);
                        Assert(committedBytes.SequenceEqual(File.ReadAllBytes(entitiesPath)), "undo preserves externally referenced generated version");
                        File.Delete(referencedPath);
                    }
                    await Json(admin, ActionPath(generated, "undo"), "{}");
                    var restoredVariant = JsonSerializer.Deserialize<YEEYEEYEE.Desktop.ProjectEntityFile>(File.ReadAllBytes(entitiesPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Entities.Single(e => e.Id == referenceEntities[1].Id).Variants[0];
                    Assert(restoredVariant.CurrentVersion == versionBefore && restoredVariant.Attachments.Count == 1, "undo restores authoritative entity variant " + outputMode);
                }
            }
            Console.WriteLine("Entity output regression passed: unique bound target, authoritative version commit, readonly/approval/auto and undo");
            var attachmentScene = await Json(admin, "/api/web/scene");
            var attachmentNode = await Json(admin, "/api/web/records", JsonSerializer.Serialize(new { baseRevision = attachmentScene.GetProperty("revision").GetInt64(), recordType = "general", title = "Attachment fixture" }));
            var attachmentNodeId = attachmentNode.GetProperty("nodeId").GetGuid();
            // 节点菜单里的「出视频」能不能点，取决于这份计划里的 canRun：视频链路在这个 fixture 里是配好的，
            // 所以一个**写了内容**的分镜必须拿到「可跑」的出视频建议——否则菜单那一项永远灰着，等于没接。
            var planScene = await Json(admin, "/api/web/scene");
            var storyboardNode = await Json(admin, "/api/web/records", JsonSerializer.Serialize(new { baseRevision = planScene.GetProperty("revision").GetInt64(), recordType = "storyboard", title = "镜头 A" }));
            var storyboardNodeId = storyboardNode.GetProperty("nodeId").GetGuid();
            var storyboardScene = await Json(admin, "/api/web/scene");
            await Json(admin, "/api/web/records/" + storyboardNodeId, JsonSerializer.Serialize(new { baseRevision = storyboardScene.GetProperty("revision").GetInt64(), title = "镜头 A", content = "主角推门进城，雨夜霓虹" }), method: "PUT");
            var storyboardPlan = await Json(admin, $"/api/web/records/{storyboardNodeId}/assist");
            var storyboardVideo = storyboardPlan.GetProperty("suggestions").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == "storyboard-video");
            Assert(storyboardVideo.GetProperty("canRun").GetBoolean() && storyboardVideo.GetProperty("blocked").GetString()!.Length == 0,
                "configured video link makes the storyboard video suggestion runnable: " + storyboardVideo);
            var attachedJob = await Json(admin, "/api/web/skills/web.text-to-video/invoke", JsonSerializer.Serialize(new { prompt = "mock attach video", seconds = 2, idempotencyKey = "attach-success", targetNodeId = attachmentNodeId }));
            var attachedJobId = attachedJob.GetProperty("jobId").GetGuid();
            await WaitAttachment(attachedJobId, state => state == "attached");
            var attachedBytes = File.ReadAllBytes(canvasPath);
            Assert(Encoding.UTF8.GetString(attachedBytes).Contains("web-job:" + attachedJobId) || Encoding.UTF8.GetString(attachedBytes).Contains("asset://"), "actual canvas output persisted");
            await Json(other, "/api/web/jobs/" + attachedJobId, status: 404);
            Stop(); await Start();
            await WaitAttachment(attachedJobId, state => state == "attached");
            Assert(File.ReadAllBytes(canvasPath).SequenceEqual(attachedBytes), "restart attachment is byte-preserving and deduplicated");
            var conflictJob = await Json(admin, "/api/web/skills/web.text-to-video/invoke", JsonSerializer.Serialize(new { prompt = "mock conflict video", seconds = 2, idempotencyKey = "attach-conflict", targetNodeId = attachmentNodeId }));
            var editScene = await Json(admin, "/api/web/scene");
            await Json(admin, "/api/web/records/" + attachmentNodeId, JsonSerializer.Serialize(new { baseRevision = editScene.GetProperty("revision").GetInt64(), title = "Later user edit", content = "Must survive background output" }), method: "PUT");
            var editedBytes = File.ReadAllBytes(canvasPath);
            // 第二个任务是从**同一版**画布提交的，其间用户还改过这个节点。
            // 回挂要按**当前快照**受保护地追加：用户那笔编辑与先前那件产物都留着——
            // 既不被旧修订挡掉（那正是原先的缺陷），也不会被覆盖掉。
            var appendedOutput = await WaitAttachment(conflictJob.GetProperty("jobId").GetGuid(), state => state == "attached");
            Assert(appendedOutput.GetProperty("state").GetString() == "Succeeded"
                && appendedOutput.GetProperty("outputs").GetArrayLength() == 1, "second output stays retained in its own job");
            var appendedCanvas = JsonDocument.Parse(File.ReadAllBytes(canvasPath)).RootElement;
            var appendedNode = appendedCanvas.GetProperty("Canvas").GetProperty("Nodes").EnumerateArray()
                .Single(node => node.GetProperty("Id").GetGuid() == attachmentNodeId);
            Assert(appendedNode.GetProperty("Title").GetString() == "Later user edit"
                && appendedNode.GetProperty("Content").GetString() == "Must survive background output", "appending keeps the later user edit");
            Assert(appendedNode.GetProperty("Attachments").GetArrayLength() == 2, "second output appends beside the first without overwriting it");
            Assert(!File.ReadAllBytes(canvasPath).SequenceEqual(editedBytes), "protected append writes the canvas exactly once");
            Console.WriteLine("Attachment background HTTP regression passed: real MP4 fixture output, canvas persistence, owner isolation, restart deduplication and shared-baseline append preserving later user edits (local backend fixture)");
            var filmCanvas = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(canvasPath))!;
            var filmNodes = filmCanvas["Canvas"]!["Nodes"]!.AsArray();
            var attachedNode = filmNodes.Single(n => n!["Id"]!.GetValue<Guid>() == attachmentNodeId)!;
            var videoReference = attachedNode["Attachments"]![0]!["Reference"]!.GetValue<string>();
            var filmInputPath = videoReference.StartsWith("asset://", StringComparison.Ordinal)
                ? Path.Combine(project, "assets", Path.GetFileName(videoReference[8..])) : videoReference;
            var filmInput = File.ReadAllBytes(filmInputPath);
            // 保留真实编码视频与字节偏移，将长度不等的音轨禁用为填充数据。
            for (var offset = 0; offset + 8 <= filmInput.Length;)
            {
                var size = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(filmInput.AsSpan(offset, 4)));
                if (size < 8 || offset + size > filmInput.Length) break;
                if (Encoding.ASCII.GetString(filmInput, offset + 4, 4) == "moov")
                    for (var track = offset + 8; track + 8 <= offset + size;)
                    {
                        var trackSize = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(filmInput.AsSpan(track, 4)));
                        if (trackSize < 8 || track + trackSize > offset + size) break;
                        if (Encoding.ASCII.GetString(filmInput, track + 4, 4) == "trak" &&
                            Encoding.ASCII.GetString(filmInput, track, trackSize).Contains("soun", StringComparison.Ordinal))
                            Encoding.ASCII.GetBytes("free").CopyTo(filmInput, track + 4);
                        track += trackSize;
                    }
                offset += size;
            }
            var filmVideoPath = Path.Combine(Path.GetDirectoryName(filmInputPath)!, "film-video-only.mp4");
            File.WriteAllBytes(filmVideoPath, filmInput);
            var shotA = Guid.NewGuid(); var shotB = Guid.NewGuid(); var productId = Guid.NewGuid();
            foreach (var shot in new[] { (Id: shotB, Title: "Second shot", X: 500), (Id: shotA, Title: "First shot", X: 100) })
            {
                var copy = attachedNode.DeepClone(); copy["Id"] = shot.Id.ToString(); copy["Category"] = 3;
                copy["Title"] = shot.Title; copy["X"] = shot.X;
                foreach (var attachment in copy["Attachments"]!.AsArray())
                {
                    attachment!["Id"] = Guid.NewGuid().ToString();
                    attachment["Reference"] = "asset://" + Path.GetFileName(filmVideoPath);
                }
                filmNodes.Add(copy);
            }
            filmNodes.Add(JsonSerializer.SerializeToNode(new { Id = productId, Title = "HTTP film", Category = 5, Attachments = Array.Empty<object>() }));
            filmCanvas["Canvas"]!["Edges"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { Id = Guid.NewGuid(), SourceNodeId = shotB, TargetNodeId = productId }));
            filmCanvas["Canvas"]!["Edges"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { Id = Guid.NewGuid(), SourceNodeId = shotA, TargetNodeId = productId }));
            File.WriteAllText(canvasPath, filmCanvas.ToJsonString());
            var filmPlan = await Json(admin, $"/api/web/records/{productId}/film");
            Assert(filmPlan.GetProperty("shots")[0].GetProperty("nodeId").GetGuid() == shotA && filmPlan.GetProperty("shots")[1].GetProperty("nodeId").GetGuid() == shotB, "film shared shot order");
            Assert(!filmPlan.GetRawText().Contains("asset://") && !filmPlan.GetRawText().Contains(canvasPath), "film plan excludes local references");
            await Json(other, $"/api/web/records/{productId}/film", JsonSerializer.Serialize(new { baseRevision = filmPlan.GetProperty("revision").GetInt64(), shotIds = new[] { shotB, shotA } }), 403);
            await Json(admin, $"/api/web/records/{productId}/film", JsonSerializer.Serialize(new { baseRevision = filmPlan.GetProperty("revision").GetInt64(), shotIds = new[] { shotA, shotA } }), 400);
            foreach (var node in filmNodes.Where(n => Guid.Parse(n!["Id"]!.ToString()) == shotA || Guid.Parse(n!["Id"]!.ToString()) == shotB))
                foreach (var attachment in node!["Attachments"]!.AsArray()) attachment!["Reference"] = videoReference;
            File.WriteAllText(canvasPath, filmCanvas.ToJsonString());
            var rejectedPlan = await Json(admin, $"/api/web/records/{productId}/film");
            var rejectedFilm = await Json(admin, $"/api/web/records/{productId}/film", JsonSerializer.Serialize(new { baseRevision = rejectedPlan.GetProperty("revision").GetInt64(), shotIds = new[] { shotB, shotA } }), 409);
            Assert(rejectedFilm.GetProperty("code").GetString() == "FILM_CONCAT_FAILED", "unequal audio/video durations rejected");
            foreach (var node in filmNodes.Where(n => Guid.Parse(n!["Id"]!.ToString()) == shotA || Guid.Parse(n!["Id"]!.ToString()) == shotB))
                foreach (var attachment in node!["Attachments"]!.AsArray()) attachment!["Reference"] = "asset://" + Path.GetFileName(filmVideoPath);
            File.WriteAllText(canvasPath, filmCanvas.ToJsonString());
            await Json(admin, $"/api/web/records/{productId}/film", JsonSerializer.Serialize(new { baseRevision = filmPlan.GetProperty("revision").GetInt64(), shotIds = new[] { shotB, shotA } }));
            await Json(admin, $"/api/web/records/{productId}/film", JsonSerializer.Serialize(new { baseRevision = filmPlan.GetProperty("revision").GetInt64(), shotIds = new[] { shotB, shotA } }), 409);
            filmPlan = await Json(admin, $"/api/web/records/{productId}/film");
            Assert(filmPlan.GetProperty("versions").GetArrayLength() == 1 && filmPlan.GetProperty("versions")[0].GetProperty("shots")[0].GetString() == "First shot", "film persisted composition follows shot order");
            using (var download = await admin.GetAsync(filmPlan.GetProperty("versions")[0].GetProperty("url").GetString()))
                Assert(download.IsSuccessStatusCode && download.Content.Headers.ContentType?.MediaType == "video/mp4" && (await download.Content.ReadAsByteArrayAsync()).Length > 16, "film MP4 download");
            await Json(admin, $"/api/web/records/{productId}/film", JsonSerializer.Serialize(new { baseRevision = filmPlan.GetProperty("revision").GetInt64(), shotIds = new[] { shotA, shotB } }));
            Assert((await Json(admin, $"/api/web/records/{productId}/film")).GetProperty("versions").GetArrayLength() == 2, "film versions append");
            Console.WriteLine("Film HTTP regression passed: shared shot order, real MP4 concatenation, permission, duplicate selection, stale revision, persisted composition, versions and download");
            Config(local: true); await Json(admin, $"/api/web/chat/sessions/{id}/messages", "{\"text\":\"local\"}", 503);
            await Json(admin, "/api/web/chat/sessions/" + id, "{}", 200, "DELETE");
            await Json(admin, "/api/web/chat/sessions/" + id, status: 404);
            var store = new WebChatStore(Path.Combine(directory, "recovery.db")); var interrupted = new WebChatSession(); interrupted.Messages.Add(new WebChatMessage { Role = "assistant", State = "streaming", Content = "partial" }); store.Save("owner", interrupted);
            var recovery = new WebChatStore(Path.Combine(directory, "recovery.db"));
            Assert(recovery.Get("owner", interrupted.Id)!.Messages[0].State == "interrupted", "crash recovery");
            Console.WriteLine("Chat regression passed: owner isolation, permissions, selected desktop configuration, OpenAI/Anthropic HTTP streams, separate thinking/usage, history, error retry, cancellation, concurrency, delete, restart and crash recovery (local fixture, not external model acceptance)");
        }
        finally { Stop(); model.Stop(); await modelLoop; }
    }
}
