using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Web;

internal static class ChatRegression
{
    public static async Task Run(string dll, string root)
    {
        static int Port() { using var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
        static void Assert(bool value, string label) { if (!value) throw new Exception("Chat regression: " + label); }
        var directory = Path.Combine(root, "chat"); Directory.CreateDirectory(directory);
        var modelPort = Port(); var webPort = Port(); var mode = "success";
        var modelText = "回答";
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
                        using var reader = new StreamReader(context.Request.InputStream);
                        var payload = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone();
                        lock (captured) captured.Add(payload);
                        var requestMode = mode;
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
                            await Frame(new { choices = new[] { new { delta = new { reasoning_content = "思考", content = modelText } } } });
                            if (requestMode == "slow") await Task.Delay(2500);
                            await Frame(new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 7, completion_tokens = 3, prompt_cache_hit_tokens = 2 } });
                        }
                        context.Response.Close();
                    }
                    catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException) { }
                });
            }
        });
        var configPath = Path.Combine(directory, "ai.json");
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
            start.Environment["YEEYEEYEE_CONFIG"] = configPath;
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
            Assert(response.IsSuccessStatusCode, "stream status");
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
            var s = await Json(admin, "/api/web/chat/sessions", "{}"); var id = s.GetProperty("id").GetString()!;
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
            mode = "fail"; var failed = await Send(id, "{\"text\":\"失败测试\"}");
            Assert(failed.GetProperty("messages")[5].GetProperty("state").GetString() == "failed", "failure persisted");
            mode = "success"; var retried = await Send(id, "{\"retry\":true}");
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
            // Model CRUD must never echo credentials, and switching only needs a revision.
            var models = await Json(admin, "/api/web/models");
            var revision = models.GetProperty("revision").GetString();
            string ModelBody(string? rev, string? secret = null) => JsonSerializer.Serialize(new { revision = rev, displayName = "Web model", endpoint = $"http://127.0.0.1:{modelPort}/v1", model = "openai-test", apiKey = secret });
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
            string MediaBody(string? rev, string? imageKey = null, string? videoKey = null, bool clear = false, string endpoint = "http://127.0.0.1:8188") =>
                JsonSerializer.Serialize(new { revision = rev, imageEndpoint = endpoint, imageModel = "image-test", comfyUiBaseUrl = "http://127.0.0.1:8188",
                    comfyUiCheckpoint = "test.safetensors", videoEndpoint = endpoint, videoModel = "video-test", imageApiKey = imageKey,
                    videoApiKey = videoKey, clearImageKey = clear, clearVideoKey = clear });
            await Json(other, "/api/web/settings/media", status: 403);
            revision = media.GetProperty("revision").GetString();
            await Json(other, "/api/web/settings/media", MediaBody(revision), 403, "PUT");
            await Json(admin, "/api/web/settings/media", MediaBody(revision, endpoint: "file:///tmp/media"), 400, "PUT");
            await Json(admin, "/api/web/settings/media", MediaBody("stale"), 409, "PUT");
            await Json(admin, "/api/web/settings/media", MediaBody(revision, "image-private-secret", "video-private-secret"), method: "PUT");
            media = await Json(admin, "/api/web/settings/media");
            var mediaDisk = File.ReadAllText(configPath);
            Assert(!mediaDisk.Contains("image-private-secret") && !mediaDisk.Contains("video-private-secret"), "media keys encrypted at rest");
            Assert(!media.ToString().Contains("private-secret") && media.GetProperty("imageHasKey").GetBoolean() && media.GetProperty("videoHasKey").GetBoolean(), "media projection exposes presence only");
            Assert(JsonDocument.Parse(mediaDisk).RootElement.GetProperty("ImageModel").GetString() == "image-test", "media writes shared desktop configuration");
            await Json(admin, "/api/web/settings/media", MediaBody(media.GetProperty("revision").GetString()), method: "PUT");
            media = await Json(admin, "/api/web/settings/media");
            Assert(media.GetProperty("imageHasKey").GetBoolean() && media.GetProperty("videoHasKey").GetBoolean(), "blank media keys preserve credentials");
            await Json(admin, "/api/web/settings/media", MediaBody(media.GetProperty("revision").GetString(), clear: true), method: "PUT");
            media = await Json(admin, "/api/web/settings/media");
            Assert(!media.GetProperty("imageHasKey").GetBoolean() && !media.GetProperty("videoHasKey").GetBoolean(), "explicit media key clearing");
            Console.WriteLine("Media settings regression passed: admin permission, URL validation, revision conflict, shared configuration persistence, encrypted keys, presence-only projection, key preservation and clearing");
            Config(); // Restore the fixture model before the remaining agent regressions.
            Console.WriteLine("Model removal regression passed: admin permission, stale revision, profile and active key clearing, deletion, selection reset and missing profile");

            async Task<(string Session, string Message)> Proposal(string executionMode = "approval", string? output = null)
            {
                modelText = output ?? "拟议修改。\n```json\n" + JsonSerializer.Serialize(new { actions = new[] { new { kind = "update_node", target = nodeId.ToString(), title = "Agent changed", content = "Agent body" } } }) + "\n```";
                var created = await Json(admin, "/api/web/chat/sessions", "{}");
                var sid = created.GetProperty("id").GetString()!;
                var scene = await Json(admin, "/api/web/scene");
                var done = await Send(sid, JsonSerializer.Serialize(new { text = "修改选中节点", mode = executionMode, nodeId, baseRevision = scene.GetProperty("revision").GetInt64() }));
                return (sid, done.GetProperty("messages")[1].GetProperty("id").GetString()!);
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
            await Json(admin, ActionPath(proposal, "preview"), "{}");
            Assert(beforeActions.SequenceEqual(File.ReadAllBytes(canvasPath)), "preview does not write");
            await Json(admin, ActionPath(proposal, "commit"), "{}");
            Assert(File.ReadAllText(canvasPath).Contains("Agent changed"), "commit writes actual desktop canvas");
            await Json(admin, ActionPath(proposal, "commit"), "{}", 409);
            await Json(admin, ActionPath(proposal, "undo"), "{}");
            Assert(File.ReadAllText(canvasPath).Contains("Selected context body"), "undo restores original content");
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
            Assert(skillSession.GetProperty("jobIds").GetArrayLength() == 0 && skillSession.GetProperty("messages")[1].GetProperty("skillState").GetString() == "preview", "model approval does not submit before approval");
            await Json(other, SkillPath(skillProposal), "{}", 404);
            var approvedJob = await Json(admin, SkillPath(skillProposal), "{}");
            skillSession = await Json(admin, "/api/web/chat/sessions/" + skillProposal.Session);
            Assert(skillSession.GetProperty("jobIds")[0].GetString() == approvedJob.GetProperty("jobId").GetString() && skillSession.GetProperty("messages")[1].GetProperty("skillState").GetString() == "submitted", "approved model job linked server-side");
            await Json(admin, SkillPath(skillProposal), "{}", 409);
            var autoSkill = await Proposal("auto", SkillOutput());
            skillSession = await Json(admin, "/api/web/chat/sessions/" + autoSkill.Session);
            Assert(skillSession.GetProperty("jobIds").GetArrayLength() == 1 && skillSession.GetProperty("messages")[1].GetProperty("skillState").GetString() == "submitted", "model auto submits and links");
            var autoJobId = skillSession.GetProperty("jobIds")[0].GetString();
            for (var i = 0; i < 100; i++)
            {
                var result = await Json(admin, "/api/web/jobs/" + autoJobId);
                if (result.GetProperty("state").GetString() == "Failed") { Assert(result.GetProperty("outputs").GetArrayLength() == 0, "failed backend has no false output"); break; }
                if (i == 99) throw new Exception("offline model skill did not fail");
                await Task.Delay(50);
            }
            var readonlySkill = await Proposal("readonly", SkillOutput());
            await Json(admin, SkillPath(readonlySkill), "{}", 403);
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
            await Send(scopedSession, JsonSerializer.Serialize(new { text = "阅读本章", chapterId = chapterOne, nodeId }));
            Assert(captured.Last().ToString().Contains(chapterOne.ToString()) && !captured.Last().ToString().Contains("Outside chapter secret"), "chapter filters node context by stable identity");
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
            Console.WriteLine("Entity HTTP regression passed: permissions, CAS, descriptions, shared commit/rollback, protected versions, media bytes/rename/detach and historical media retention");
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
            var attachmentScene = await Json(admin, "/api/web/scene");
            var attachmentNode = await Json(admin, "/api/web/records", JsonSerializer.Serialize(new { baseRevision = attachmentScene.GetProperty("revision").GetInt64(), recordType = "general", title = "Attachment fixture" }));
            var attachmentNodeId = attachmentNode.GetProperty("nodeId").GetGuid();
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
            var blockedOutput = await WaitAttachment(conflictJob.GetProperty("jobId").GetGuid(), state => state.Contains("REVISION_CONFLICT"));
            Assert(blockedOutput.GetProperty("state").GetString() == "Succeeded" && blockedOutput.GetProperty("outputs").GetArrayLength() == 1, "conflicting output retained in job");
            Assert(File.ReadAllBytes(canvasPath).SequenceEqual(editedBytes), "revision conflict preserves later user edit bytes");
            Console.WriteLine("Attachment background HTTP regression passed: real MP4 fixture output, canvas persistence, owner isolation, restart deduplication and later-edit revision conflict (local backend fixture)");
            var filmCanvas = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(canvasPath))!;
            var filmNodes = filmCanvas["Canvas"]!["Nodes"]!.AsArray();
            var attachedNode = filmNodes.Single(n => n!["Id"]!.GetValue<Guid>() == attachmentNodeId)!;
            var videoReference = attachedNode["Attachments"]![0]!["Reference"]!.GetValue<string>();
            var filmInputPath = videoReference.StartsWith("asset://", StringComparison.Ordinal)
                ? Path.Combine(project, "assets", Path.GetFileName(videoReference[8..])) : videoReference;
            var filmInput = File.ReadAllBytes(filmInputPath);
            // Keep the real encoded video and byte offsets; disable the unequal audio track as padding.
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
