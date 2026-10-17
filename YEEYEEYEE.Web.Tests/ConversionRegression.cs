using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Web.Auth;

internal static class ConversionRegression
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "yeeeyee-conversion-tests-" + Guid.NewGuid().ToString("N"));
        var previousSkills = Environment.GetEnvironmentVariable("YEEYEEYEE_SKILL_DIR");
        var configPath = typeof(YEEYEEYEE.Web.WebChatMessage).Assembly.GetType("YEEYEEYEE.Web.WebSettingsApi")!
            .GetProperty("ServerConfigPath", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousConfig = configPath.GetValue(null);
        try
        {
            configPath.SetValue(null, Path.Combine(root, "unused-config.json"));
            Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", Path.Combine(root, "skills"));
            Check(Path.GetFullPath(SiteCatalog.Directory) == Path.Combine(root, "skills", "sites"), "isolated site catalog path");
            await RunIsolated();
        }
        finally
        {
            configPath.SetValue(null, previousConfig);
            Environment.SetEnvironmentVariable("YEEYEEYEE_SKILL_DIR", previousSkills);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunIsolated()
    {
        await using var upstream = new MockComfyUiServer();
        await upstream.StartAsync();
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        var main = $"http://main.invalid:{port}";
        var conversionOrigin = $"http://convert.invalid:{port}";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ComfyUIConversion:MainOrigin"] = main,
            ["ComfyUIConversion:ConversionOrigin"] = conversionOrigin,
            ["ComfyUI:BaseUrl"] = new Uri(upstream.BaseAddress, "api/").AbsoluteUri,
            ["ComfyUI:Authorization"] = "Bearer server-only"
        });
        await using var app = builder.Build();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = typeof(YEEYEEYEE.Web.WebChatMessage).Assembly.GetType("YEEYEEYEE.Web.ComfyUiConversion")!;
        using var conversion = (IDisposable)Activator.CreateInstance(type, flags, null, new object[] { builder.Configuration, app.Environment }, null)!;
        var owner = new WebUser(Guid.NewGuid(), "conversion-owner", "Owner", UserRole.Admin, DateTimeOffset.UtcNow, null, false);
        var otherAdmin = owner with { Id = Guid.NewGuid(), Username = "conversion-other" };
        var editor = owner with { Role = UserRole.Editor };
        var userItem = typeof(YEEYEEYEE.Web.WebChatMessage).Assembly.GetType("YEEYEEYEE.Web.WebAccessGuard")!
            .GetField("UserItem")!.GetRawConstantValue()!.ToString()!;
        app.Use(async (context, next) =>
        {
            var user = context.Request.Headers["X-Conversion-Test-User"].ToString() switch
            {
                "owner" => owner,
                "other" => otherAdmin,
                "editor" => editor,
                _ => null
            };
            if (user is not null) context.Items[userItem] = user;
            await next(context);
        });
        type.GetMethod("Map", flags)!.Invoke(conversion, new object[] { app });
        app.MapGet("/api/auth/state", () => "main-auth");
        app.MapGet("/api/web/private", () => "main-private");
        var sessionType = type.GetNestedType("Session", BindingFlags.NonPublic)!;
        var target = new Uri(upstream.BaseAddress, "api/");
        var session = Activator.CreateInstance(sessionType, flags, null, new object[] { Guid.NewGuid(), target, "" }, null)!;
        object Get(string name) => sessionType.GetProperty(name, flags)!.GetValue(session)!;
        void Set(string name, object value) => sessionType.GetProperty(name, flags)!.SetValue(session, value);
        var id = (string)Get("Id");
        var controlHost = $"{id}.convert.invalid:{port}";
        var workerHost = $"w0-{id}.convert.invalid:{port}";
        Set("Origin", "http://" + controlHost);
        Set("Http", type.GetMethod("Client", flags)!.Invoke(conversion, new object[] { target })!);
        Set("Result", new TaskCompletionSource<ComfyUiLibraryResult>().Task);
        var sessions = type.GetField("sessions", flags)!.GetValue(conversion)!;
        sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, new[] { id, session });
        app.Urls.Add($"http://127.0.0.1:{port}");
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(startup.Token);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
        async Task<HttpResponseMessage> Request(string path, string? host = null, HttpMethod? method = null, object? body = null,
            string? user = null, string? origin = null)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, $"http://127.0.0.1:{port}" + path);
            request.Headers.Host = host ?? workerHost;
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer browser-secret");
            request.Headers.TryAddWithoutValidation("Cookie", "main-secret=1");
            if (user is not null) request.Headers.TryAddWithoutValidation("X-Conversion-Test-User", user);
            var requestOrigin = origin ?? (body is not null ? "http://" + controlHost : null);
            if (!string.IsNullOrEmpty(requestOrigin)) request.Headers.TryAddWithoutValidation("Origin", requestOrigin);
            if (body is not null) request.Content = JsonContent.Create(body);
            return await http.SendAsync(request);
        }
        try
        {
            foreach (var path in new[] { "/api/auth/state", "/api/web/private", "/__conversion/scripts" })
            {
                using var response = await Request(path);
                Check(response.StatusCode == HttpStatusCode.NotFound, "worker isolation: " + path);
            }
            foreach (var host in new[] { "bad.invalid:" + port, "w1-" + id + ".convert.invalid:" + port, workerHost.Replace(":" + port, ":1") })
            {
                using var response = await Request("/api/headers", host);
                Check(response.StatusCode == HttpStatusCode.NotFound, "host validation");
            }
            using (var response = await Request("/api/headers"))
            {
                var headers = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                Check(headers["authorization"]?.GetValue<string>() == "Bearer server-only", "server authentication");
                Check(headers["cookie"] is null && headers["origin"] is null, "request header isolation");
                Check(!response.Headers.Contains("Set-Cookie") && !response.Headers.Contains("X-Upstream-Secret"), "response header isolation");
            }
            using (var response = await Request("/api/redirect-relative"))
                Check(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.OriginalString == "/api/assets/main.js", "relative redirect and prefix");
            using (var response = await Request("/api/redirect-external"))
                Check(response.StatusCode == HttpStatusCode.BadGateway, "external redirect rejected");
            using (var response = await Request("/api/assets/main.js"))
            {
                var script = await response.Content.ReadAsStringAsync();
                Check(script.Contains("http://" + workerHost) && script.Contains("ws://" + workerHost) && !script.Contains(upstream.BaseAddress.AbsoluteUri), "absolute URL rewriting");
            }
            using (var response = await Request("/api/prompt")) Check(response.StatusCode == HttpStatusCode.Forbidden, "generation blocked");
            using (var response = await Request("/api/headers", method: HttpMethod.Post)) Check(response.StatusCode == HttpStatusCode.MethodNotAllowed, "writes blocked");
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Host", workerHost);
            socket.Options.SetRequestHeader("Origin", "http://" + workerHost);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/ws"), deadline.Token);
            var payload = Encoding.UTF8.GetBytes("bidirectional");
            await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
            var buffer = new byte[128];
            var received = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token);
            Check(Encoding.UTF8.GetString(buffer, 0, received.Count) == "bidirectional", "websocket round trip");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
            var workType = type.GetNestedType("Work", BindingFlags.NonPublic)!;
            foreach (var mode in new[] { "invalid", "error", "valid" })
            {
                var requestId = Guid.NewGuid().ToString("N");
                var work = Activator.CreateInstance(workType, flags, null, new object[] { requestId, 0, id, "hash", "{}" }, null)!;
                var pending = Get("Pending");
                pending.GetType().GetMethod("TryAdd")!.Invoke(pending, new[] { requestId, work });
                var data = new JsonObject { ["kind"] = "library-export", ["requestId"] = requestId, ["session"] = id, ["worker"] = 0, ["sourceHash"] = "hash" };
                if (mode == "error") data["error"] = "missing custom node";
                if (mode == "valid")
                {
                    data["identityVerified"] = true;
                    data["fullOfficialLoad"] = true;
                    data["inspection"] = new JsonObject();
                    data["api"] = JsonNode.Parse("{\"1\":{\"class_type\":\"SaveImage\",\"inputs\":{}}}");
                }
                using var response = await Request("/__conversion/result", controlHost, HttpMethod.Post, data);
                Check(response.StatusCode == HttpStatusCode.NoContent, "result accepted: " + mode);
                var completion = workType.GetProperty("Completion", flags)!.GetValue(work)!;
                var task = (Task<JsonObject>)completion.GetType().GetProperty("Task")!.GetValue(completion)!;
                if (mode == "valid") Check((await task).Count == 1, "valid API graph");
                else
                {
                    Check(task.IsFaulted, "invalid result completes immediately");
                    Check(task.Exception!.GetBaseException().Message.Contains(mode == "error" ? "missing custom node" : "API"), "concrete result error");
                }
            }
            using (var response = await Request("/api/web/settings/workflow-conversions", "main.invalid:" + port, HttpMethod.Post, new { paths = new[] { "api.json" } }))
                Check(response.StatusCode == HttpStatusCode.Forbidden, "unauthorized creation");
            const string conversionsPath = "/api/web/settings/workflow-conversions";
            var mainHost = new Uri(main).Authority;
            var selection = new { paths = new[] { "video/ui.json" }, baseUrl = target.AbsoluteUri };
            foreach (var origin in new[] { "", "null", conversionOrigin, main + "/", main.Replace("http:", "https:"), "http://outside.invalid" })
            {
                using var response = await Request(conversionsPath, mainHost, HttpMethod.Post, selection, "owner", origin);
                Check(response.StatusCode == HttpStatusCode.Forbidden, "creation Origin rejected: " + origin);
            }
            using (var response = await Request(conversionsPath, mainHost, HttpMethod.Post, selection, "editor", main))
                Check(response.StatusCode == HttpStatusCode.Forbidden, "non-admin creation rejected");
            foreach (var invalidTarget in new[]
            {
                "relative/path", "file:///tmp/comfy", "ftp://upstream.invalid/", "http://user:password@upstream.invalid/",
                target.AbsoluteUri + "?token=secret", target.AbsoluteUri + "#fragment", main, conversionOrigin,
                "http://child.main.invalid/", "http://w0-session.convert.invalid/", "http://invalid/"
            })
            {
                using var response = await Request(conversionsPath, mainHost, HttpMethod.Post,
                    new { paths = selection.paths, baseUrl = invalidTarget }, "owner", main);
                Check(response.StatusCode == HttpStatusCode.BadRequest, "illegal target rejected: " + invalidTarget);
            }
            foreach (var paths in new[] { Array.Empty<string>(), new[] { " " }, Enumerable.Repeat("video/ui.json", 501).ToArray() })
            {
                using var response = await Request(conversionsPath, mainHost, HttpMethod.Post, new { paths, baseUrl = target.AbsoluteUri }, "owner", main);
                Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid path selection rejected");
            }

            object FindSession(string sessionId) => sessions.GetType().GetProperty("Item")!.GetValue(sessions, new object[] { sessionId })!;
            async Task<JsonObject> Create()
            {
                using var response = await Request(conversionsPath, mainHost, HttpMethod.Post, selection, "owner", main);
                Check(response.StatusCode == HttpStatusCode.OK, "administrator HTTP creation");
                var created = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
                var createdId = created["id"]!.GetValue<string>();
                Check(Guid.TryParseExact(createdId, "N", out _) && Guid.TryParseExact(created["requestId"]!.GetValue<string>(), "N", out _), "created session identity");
                Check(created["origin"]!.GetValue<string>() == $"http://{createdId}.convert.invalid:{port}" &&
                    created["url"]!.GetValue<string>() == created["origin"]!.GetValue<string>() + "/__conversion/", "created isolated origin");
                Check((Guid)sessionType.GetProperty("Owner", flags)!.GetValue(FindSession(createdId))! == owner.Id, "created session owner");
                return created;
            }

            async Task WaitForPending(object activeSession)
            {
                var pending = sessionType.GetProperty("Pending", flags)!.GetValue(activeSession)!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while ((int)pending.GetType().GetProperty("Count")!.GetValue(pending)! == 0)
                    await Task.Delay(25, timeout.Token);
            }

            var created = await Create();
            var createdId = created["id"]!.GetValue<string>();
            var createdOrigin = created["origin"]!.GetValue<string>();
            var createdHost = new Uri(createdOrigin).Authority;
            var completePath = conversionsPath + "/" + createdId + "/complete";
            var deletePath = conversionsPath + "/" + createdId;
            foreach (var user in new string?[] { null, "other", "editor" })
            {
                using (var response = await Request(completePath, mainHost, HttpMethod.Post, user: user, origin: main))
                    Check(response.StatusCode == HttpStatusCode.Forbidden, "completion ownership/role rejected: " + user);
                using (var response = await Request(deletePath, mainHost, HttpMethod.Delete, user: user, origin: main))
                    Check(response.StatusCode == HttpStatusCode.Forbidden, "deletion ownership/role rejected: " + user);
            }
            using (var response = await Request(completePath, mainHost, HttpMethod.Post, user: "owner", origin: main))
                Check(response.StatusCode == HttpStatusCode.Conflict, "completion waits for UI work");
            Check(SiteCatalog.Load().Sites.Count == 0, "rejections do not install sites");

            using var workDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            JsonObject queued;
            while (true)
            {
                workDeadline.Token.ThrowIfCancellationRequested();
                using var response = await Request("/__conversion/next", createdHost);
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    await Task.Delay(25, workDeadline.Token);
                    continue;
                }
                Check(response.StatusCode == HttpStatusCode.OK, "queued UI work available");
                queued = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
                Check(queued["RequestId"] is not null, "UI export requested before completion");
                break;
            }
            Check(queued["Session"]!.GetValue<string>() == createdId && queued["WorkerId"]!.GetValue<int>() == 0 &&
                JsonNode.Parse(queued["Raw"]!.GetValue<string>())!["nodes"] is JsonArray, "selected UI draft queued");
            var raw = queued["Raw"]!.GetValue<string>();
            Check(queued["SourceHash"]!.GetValue<string>() == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(), "queued source hash");
            var mockResult = new JsonObject
            {
                ["kind"] = "library-export", ["requestId"] = queued["RequestId"]!.GetValue<string>(),
                ["session"] = createdId, ["worker"] = 0, ["sourceHash"] = queued["SourceHash"]!.GetValue<string>(),
                ["identityVerified"] = true, ["fullOfficialLoad"] = true, ["inspection"] = new JsonObject(),
                ["api"] = JsonNode.Parse("{\"1\":{\"class_type\":\"SaveImage\",\"inputs\":{\"filename_prefix\":\"mock-conversion\"}}}")
            };
            using (var response = await Request("/__conversion/result", createdHost, HttpMethod.Post, mockResult, origin: main))
                Check(response.StatusCode == HttpStatusCode.NotFound, "cross-origin mock result rejected");
            foreach (var field in new[] { "requestId", "session", "worker", "sourceHash" })
            {
                var mismatched = (JsonObject)mockResult.DeepClone();
                if (field == "worker") mismatched[field] = 1;
                else mismatched[field] = "mismatch";
                using var response = await Request("/__conversion/result", createdHost, HttpMethod.Post, mismatched, origin: createdOrigin);
                Check(response.StatusCode == HttpStatusCode.BadRequest, "mock result identity rejected: " + field);
            }
            using (var response = await Request("/__conversion/result", createdHost, HttpMethod.Post, mockResult, origin: createdOrigin))
                Check(response.StatusCode == HttpStatusCode.NoContent, "mock UI result accepted");
            var resultTask = (Task<ComfyUiLibraryResult>)sessionType.GetProperty("Result", flags)!.GetValue(FindSession(createdId))!;
            var fetched = await resultTask.WaitAsync(workDeadline.Token);
            Check(fetched.Converted == 1 && fetched.Failed == 0 && fetched.Workflows.Single().Key == "video/ui.json", "mock UI conversion result");
            using (var response = await Request(completePath, mainHost, HttpMethod.Post, user: "owner", origin: main))
            {
                Check(response.StatusCode == HttpStatusCode.OK, "HTTP complete installs mock conversion");
                var completed = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                Check(completed["converted"]!.GetValue<int>() == 1 && completed["failed"]!.GetValue<int>() == 0 &&
                    completed["workflows"]!.AsArray().Single()!["key"]!.GetValue<string>() == "video/ui.json", "installation response counts and selection");
            }
            var installed = SiteCatalog.Load().Sites.Single();
            var installedWorkflow = installed.Workflows.Single();
            Check(installed.BaseUrl.TrimEnd('/') == target.AbsoluteUri.TrimEnd('/') && installedWorkflow.Key == "video/ui.json" && installedWorkflow.Converted, "isolated site installed");
            Check(JsonNode.DeepEquals(JsonNode.Parse(SiteCatalog.LoadPayload(installed.Id, installedWorkflow.PayloadFile)!), mockResult["api"]), "installed mock API payload");
            Check(JsonNode.Parse(SiteCatalog.LoadPayload(installed.Id, installedWorkflow.RawDraftFile)!)!["nodes"] is JsonArray, "installed original UI draft");
            using (var response = await Request(completePath, mainHost, HttpMethod.Post, user: "owner", origin: main))
                Check(response.StatusCode == HttpStatusCode.Forbidden, "completed session cannot be reused");
            using (var response = await Request("/__conversion/next", createdHost))
                Check(response.StatusCode == HttpStatusCode.NotFound, "completed control removed");

            var expiring = await Create();
            var expiredId = expiring["id"]!.GetValue<string>();
            var expiredSession = FindSession(expiredId);
            await WaitForPending(expiredSession);
            var stop = (CancellationTokenSource)sessionType.GetProperty("Stop", flags)!.GetValue(expiredSession)!;
            stop.Cancel(); // Expiration uses the same cancellation source; avoid waiting fifteen minutes.
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Delete })
            {
                var path = conversionsPath + "/" + expiredId + (method == HttpMethod.Post ? "/complete" : "");
                using var response = await Request(path, mainHost, method, user: "owner", origin: main);
                Check(response.StatusCode == HttpStatusCode.Forbidden, "expired owner request rejected: " + method);
            }
            foreach (var host in new[] { new Uri(expiring["origin"]!.GetValue<string>()).Authority, $"w0-{expiredId}.convert.invalid:{port}" })
            {
                using var response = await Request("/__conversion/next", host);
                Check(response.StatusCode == HttpStatusCode.NotFound, "expired conversion host rejected");
            }
            var expiredResult = (Task<ComfyUiLibraryResult>)sessionType.GetProperty("Result", flags)!.GetValue(expiredSession)!;
            try { await expiredResult.WaitAsync(TimeSpan.FromSeconds(10)); Check(false, "expired fetch must cancel"); }
            catch (OperationCanceledException) { }
            Check(SiteCatalog.Load().Sites.Single().Workflows.Count == 1, "expiry does not alter installed site");

            var deletable = await Create();
            var deletedSession = FindSession(deletable["id"]!.GetValue<string>());
            await WaitForPending(deletedSession);
            using (var response = await Request(conversionsPath + "/" + deletable["id"]!.GetValue<string>(), mainHost, HttpMethod.Delete, user: "owner", origin: main))
                Check(response.StatusCode == HttpStatusCode.NoContent, "owner can delete session");
            using (var response = await Request("/__conversion/next", new Uri(deletable["origin"]!.GetValue<string>()).Authority))
                Check(response.StatusCode == HttpStatusCode.NotFound, "deleted control removed");
            var deletedResult = (Task<ComfyUiLibraryResult>)sessionType.GetProperty("Result", flags)!.GetValue(deletedSession)!;
            try { await deletedResult.WaitAsync(TimeSpan.FromSeconds(10)); Check(false, "deleted fetch must cancel"); }
            catch (OperationCanceledException) { }
            sessionType.GetMethod("Cancel", flags)!.Invoke(session, null);
            using (var response = await Request("/__conversion/next", controlHost)) Check(response.StatusCode == HttpStatusCode.NotFound, "cancelled session");
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("ComfyUI conversion HTTP/WS regression passed");
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("Conversion regression failed: " + name);
    }
}
