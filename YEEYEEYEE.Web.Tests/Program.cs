using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var root = Path.Combine(Path.GetTempPath(), "yeeeyee-web-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var scenePath = Path.Combine(root, "scene.json");
var claimsConfigPath = Path.Combine(root, "appsettings.json");
void SetClaims(string claims) => File.WriteAllText(claimsConfigPath,
    JsonSerializer.Serialize(new { YEEYEEYEE = new { WebClaims = claims } }));
var assetsPath = Path.Combine(root, "entities.json");
var id = Guid.NewGuid().ToString();
var other = Guid.NewGuid().ToString();
File.WriteAllText(scenePath, JsonSerializer.Serialize(new { revision = 4, records = new object[] {
    new { recordId = id, recordType = "Shot", parentId = other, record = new { title = "Old", content = "Original", reference = new { targetId = other } } },
    new { recordId = other, recordType = "Chapter", record = new { title = "Other", content = "Untouched" } }
} }));
File.WriteAllText(assetsPath, "{\"entities\":[{\"entityId\":\"asset-1\",\"name\":\"角色\",\"kind\":\"Character\"}]}");
var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "YEEYEEYEE.Web.dll"));
if (!File.Exists(dll)) throw new Exception("Web assembly missing");
Process? server = null;
var port = 0;
async Task Start(string? token = "secret-value", string claims = "canvas.edit,skill.invoke,job.cancel", string approval = "preapproved-local-image", bool standalone = true, string? projectCanvas = null, string? userDatabase = null, string? setupToken = null, int? leaseSeconds = null)
{
    SetClaims(claims);
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    var start = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls http://127.0.0.1:{port}")
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        WorkingDirectory = root
    };
    start.Environment["YEEYEEYEE__JobDatabasePath"] = Path.Combine(root, "jobs.db");
    start.Environment["YEEYEEYEE__WebScenePath"] = scenePath;
    start.Environment["YEEYEEYEE__AllowStandaloneWebScene"] = standalone.ToString();
    if (projectCanvas is not null) start.Environment["YEEYEEYEE__ProjectCanvasPath"] = projectCanvas;
    start.Environment["YEEYEEYEE__ProjectEntitiesPath"] = projectCanvas is not null &&
        Path.GetFileName(Path.GetDirectoryName(projectCanvas)) == "canvases"
        ? Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(projectCanvas)!)!, "project", "entities.json") : assetsPath;
    start.Environment["YEEYEEYEE__WebToken"] = token;
    // 账号库单独一个文件：每个账号用例都能从一个空库开始，不跟别的用例互相影响。
    start.Environment["YEEYEEYEE__UserDatabasePath"] = userDatabase ?? Path.Combine(root, "users.db");
    if (setupToken is null) start.Environment.Remove("YEEYEEYEE__SetupToken");
    else start.Environment["YEEYEEYEE__SetupToken"] = setupToken;
    // 编辑锁的有效期可配：默认两分钟，测过期时缩到几秒，否则用例得干等两分钟。
    if (leaseSeconds is null) start.Environment.Remove("YEEYEEYEE__EditLeaseLifetimeSeconds");
    else start.Environment["YEEYEEYEE__EditLeaseLifetimeSeconds"] = leaseSeconds.Value.ToString();
    start.Environment["ComfyUI__BaseUrl"] = "http://127.0.0.1:8188";
    start.Environment["ComfyUI__Checkpoint"] = "offline-model.safetensors";
    // appsettings.json reloads in the running server, allowing real HTTP revocation tests.
    start.Environment["YEEYEEYEE__WebSkillApprovalMode"] = approval;
    server = Process.Start(start)!;
    using var probe = new HttpClient();
    for (var i = 0; i < 100; i++)
    {
        if (server.HasExited) throw new Exception("Server exited: " + await server.StandardError.ReadToEndAsync());
        try { if ((await probe.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode) return; }
        catch (HttpRequestException) { }
        await Task.Delay(100);
    }
    throw new Exception("Server startup timed out");
}
void Stop()
{
    // 幂等：重复调用、以及进程已经被上一次调用释放过，都不该抛异常。
    // （早些时候没这一步，最后那个 finally 里的 Stop() 会对已释放的对象取 HasExited 而崩掉，
    //   于是「两套回归都通过」之后整个测试进程仍以非零码退出。）
    if (server is null) return;
    try
    {
        if (!server.HasExited) { server.Kill(entireProcessTree: true); server.WaitForExit(); }
    }
    catch (InvalidOperationException) { }
    server.Dispose();
    server = null;
}
async Task<JsonElement> Check(HttpClient client, HttpMethod method, string path, int status, string? json = null)
{
    using var request = new HttpRequestMessage(method, path);
    if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
    using var response = await client.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    if ((int)response.StatusCode != status) throw new Exception($"{method} {path}: {(int)response.StatusCode} expected {status}: {text}");
    var result = JsonDocument.Parse(text).RootElement.Clone();
    if (status >= 400 && (!result.TryGetProperty("code", out _) || !result.TryGetProperty("message", out _)))
        throw new Exception("Missing error contract: " + text);
    return result;
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task WaitForEditClaim(HttpClient client, string recordId, bool allowed)
{
    for (var i = 0; i < 100; i++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/web/records/{recordId}")
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if ((int)response.StatusCode == (allowed ? 400 : 403) &&
            document.RootElement.GetProperty("code").GetString() == (allowed ? "INVALID_REQUEST" : "CANVAS_EDIT_FORBIDDEN")) return;
        await Task.Delay(100);
    }
    throw new Exception($"Timed out waiting for canvas.edit {(allowed ? "grant" : "revocation")}");
}
/// <summary>从 SSE 流里读下一帧。读到空行才算一帧读完；返回 null 表示流关了。</summary>
async Task<(string Type, JsonElement Data)?> ReadFrame(StreamReader reader)
{
    var type = (string?)null;
    var data = new StringBuilder();
    while (true)
    {
        var line = await reader.ReadLineAsync();
        if (line is null) return null;
        if (line.Length == 0)
        {
            // 开头那行 retry（重连间隔）与心跳 `: ping` 都没有 event:，于是到这里什么都不返回，
            // 继续等下一帧——它们本来就不该被当成事件。
            if (type is not null) return (type, JsonDocument.Parse(data.ToString()).RootElement.Clone());
            continue;
        }
        if (line[0] == ':') continue;
        if (line.StartsWith("event:", StringComparison.Ordinal)) type = line[6..].Trim();
        else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(line[5..].Trim());
    }
}
try
{
    await Start();
    using var anonymous = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    using var wrong = new HttpClient { BaseAddress = anonymous.BaseAddress };
    wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
    using var authorized = new HttpClient { BaseAddress = anonymous.BaseAddress };
    authorized.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-value");
    var before = File.ReadAllText(scenePath);
    await Check(anonymous, HttpMethod.Get, "/api/web/skills", 401);
    await Check(wrong, HttpMethod.Get, "/api/web/jobs", 401);
    await Check(anonymous, HttpMethod.Post, "/api/web/skills/comfyui.text-to-image/invoke", 401, "{}");
    await Check(anonymous, HttpMethod.Post, $"/api/web/jobs/{Guid.NewGuid()}/cancel", 401);
    await Check(anonymous, HttpMethod.Post, $"/api/web/jobs/{Guid.NewGuid()}/retry", 401);
    var catalog = await Check(authorized, HttpMethod.Get, "/api/web/skills", 200);
    Assert(catalog.GetProperty("skills").GetArrayLength() == 1, "Approved catalog");
    await Check(authorized, HttpMethod.Post, "/api/web/skills/unknown/invoke", 404, "{}");
    await Check(authorized, HttpMethod.Post, "/api/web/skills/comfyui.text-to-image/invoke", 400, "{\"prompt\":\"x\",\"idempotencyKey\":\"a\",\"tool\":\"unsafe\"}");
    await Check(authorized, HttpMethod.Get, $"/api/web/jobs/{Guid.NewGuid()}", 404);
    await Check(authorized, HttpMethod.Post, $"/api/web/jobs/{Guid.NewGuid()}/cancel", 404);
    await Check(authorized, HttpMethod.Post, $"/api/web/jobs/{Guid.NewGuid()}/retry", 404);
    var invoked = await Check(authorized, HttpMethod.Post, "/api/web/skills/comfyui.text-to-image/invoke", 200, "{\"prompt\":\"offline test\",\"idempotencyKey\":\"test-run-1\"}");
    var jobId = invoked.GetProperty("jobId").GetGuid();
    Assert((await Check(authorized, HttpMethod.Post, "/api/web/skills/comfyui.text-to-image/invoke", 200, "{\"prompt\":\"offline test\",\"idempotencyKey\":\"test-run-1\"}")).GetProperty("jobId").GetGuid() == jobId, "Idempotent invoke");
    await Check(authorized, HttpMethod.Post, "/api/web/skills/comfyui.text-to-image/invoke", 409, "{\"prompt\":\"different\",\"idempotencyKey\":\"test-run-1\"}");
    var readJob = await Check(authorized, HttpMethod.Get, $"/api/web/jobs/{jobId}", 200);
    Assert(readJob.GetProperty("jobId").GetGuid() == jobId, "Job details");
    var list = await Check(authorized, HttpMethod.Get, "/api/web/jobs", 200);
    Assert(list.GetProperty("jobs").GetArrayLength() == 1, "Job list");
    // No ComfyUI server is running: real executor must report a failed task, not synthetic success.
    for (var i = 0; i < 30 && readJob.GetProperty("state").GetString() != "Failed"; i++)
    {
        await Task.Delay(100);
        readJob = await Check(authorized, HttpMethod.Get, $"/api/web/jobs/{jobId}", 200);
    }
    Assert(readJob.GetProperty("state").GetString() == "Failed" && readJob.GetProperty("errorCode").GetString() == "JOB_EXECUTION_FAILED", "Real failure not surfaced");
    await Check(authorized, HttpMethod.Post, $"/api/web/jobs/{jobId}/cancel", 409);
    var retried = await Check(authorized, HttpMethod.Post, $"/api/web/jobs/{jobId}/retry", 200);
    Assert(retried.GetProperty("retryOfJobId").GetGuid() == jobId && retried.GetProperty("attempt").GetInt32() == 2, "Retry ancestry");
    var retryJobId = retried.GetProperty("jobId").GetGuid();
    for (var i = 0; i < 30 && (await Check(authorized, HttpMethod.Get, $"/api/web/jobs/{retryJobId}", 200)).GetProperty("state").GetString() != "Failed"; i++) await Task.Delay(100);
    var third = await Check(authorized, HttpMethod.Post, $"/api/web/jobs/{jobId}/retry", 200);
    Assert(third.GetProperty("attempt").GetInt32() == 3, "Chain attempt numbering");
    var thirdId = third.GetProperty("jobId").GetGuid();
    for (var i = 0; i < 30 && (await Check(authorized, HttpMethod.Get, $"/api/web/jobs/{thirdId}", 200)).GetProperty("state").GetString() != "Failed"; i++) await Task.Delay(100);
    await Check(authorized, HttpMethod.Post, $"/api/web/jobs/{jobId}/retry", 409);
    await Check(anonymous, HttpMethod.Put, $"/api/web/records/{id}", 401, "{\"baseRevision\":4,\"title\":\"bad\",\"content\":\"bad\"}");
    await Check(wrong, HttpMethod.Get, "/api/web/scene", 401);
    await Check(anonymous, HttpMethod.Post, "/api/canvas/scene", 401, "{\"revision\":999,\"records\":[]}");
    await Check(anonymous, HttpMethod.Post, "/api/canvas/nodes", 401, "{}");
    await Check(anonymous, HttpMethod.Post, "/api/canvas/resource-replace", 401, "{}");
    await Check(anonymous, HttpMethod.Post, "/api/canvas/resource-replace/result", 401, "{}");
    await Check(anonymous, HttpMethod.Get, "/api/canvas/resource-replace/next", 401);
    Assert(before == File.ReadAllText(scenePath), "Denied requests changed scene");
    var initial = await Check(authorized, HttpMethod.Get, "/api/web/scene", 200);
    Assert(initial.GetProperty("revision").GetInt32() == 4, "Initial revision");
    // Revoke in the running process: the same Bearer remains valid for GET, not PUT.
    SetClaims("skill.invoke,job.cancel");
    await WaitForEditClaim(authorized, id, allowed: false);
    var deniedStandaloneBytes = File.ReadAllBytes(scenePath);
    Assert((await Check(authorized, HttpMethod.Put, $"/api/web/records/{id}", 403,
        "{\"baseRevision\":4,\"title\":\"Denied\",\"content\":\"Denied\"}"))
        .GetProperty("code").GetString() == "CANVAS_EDIT_FORBIDDEN", "Standalone edit without claim");
    Assert((await Check(authorized, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt32() == 4, "Revoked standalone GET");
    Assert(deniedStandaloneBytes.SequenceEqual(File.ReadAllBytes(scenePath)), "Revoked standalone PUT changed bytes");
    SetClaims("canvas.edit,skill.invoke,job.cancel");
    await WaitForEditClaim(authorized, id, allowed: true);
    var assets = await Check(authorized, HttpMethod.Get, "/api/web/assets", 200);
    Assert(assets.GetProperty("entities")[0].GetProperty("entityId").GetString() == "asset-1", "Assets mapping");
    File.WriteAllText(assetsPath, "not JSON");
    await Check(authorized, HttpMethod.Get, "/api/web/assets", 500);
    await Check(authorized, HttpMethod.Put, $"/api/web/records/{id}", 409, "{\"baseRevision\":3,\"title\":\"bad\",\"content\":\"bad\"}");
    await Check(authorized, HttpMethod.Put, "/api/web/records/missing", 404, "{\"baseRevision\":4,\"title\":\"bad\",\"content\":\"bad\"}");
    await Check(authorized, HttpMethod.Put, $"/api/web/records/{id}", 400, "{\"baseRevision\":4,\"title\":null}");
    Assert(before == File.ReadAllText(scenePath), "Rejected write changed scene");
    var updated = await Check(authorized, HttpMethod.Put, $"/api/web/records/{id}", 200, "{\"baseRevision\":4,\"title\":\"New\",\"content\":\"Edited\"}");
    Assert(updated.GetProperty("revision").GetInt32() == 5 && updated.GetProperty("record").GetProperty("recordId").GetString() == id, "Write response");
    await Check(authorized, HttpMethod.Put, $"/api/web/records/{id}", 409, "{\"baseRevision\":4,\"title\":\"stale\",\"content\":\"stale\"}");
    var saved = await Check(authorized, HttpMethod.Get, "/api/web/scene", 200);
    Assert(saved.GetProperty("records")[0].GetProperty("parentId").GetString() == other && saved.GetProperty("records")[0].GetProperty("record").GetProperty("reference").GetProperty("targetId").GetString() == other, "References changed");
    Assert(saved.GetProperty("records")[1].GetProperty("record").GetProperty("title").GetString() == "Other", "Unrelated record changed");
    Stop();
    await Start();
    using var restarted = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    restarted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-value");
    var persisted = await Check(restarted, HttpMethod.Get, "/api/web/scene", 200);
    Assert(persisted.GetProperty("revision").GetInt32() == 5 && persisted.GetProperty("records")[0].GetProperty("record").GetProperty("content").GetString() == "Edited", "Restart persistence");
    Assert((await Check(restarted, HttpMethod.Get, $"/api/web/jobs/{jobId}", 200)).GetProperty("jobId").GetGuid() == jobId, "Job restart persistence");
    Stop();
    await Start(claims: "job.cancel", approval: "");
    using var restricted = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    restricted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-value");
    Assert((await Check(restricted, HttpMethod.Get, "/api/web/skills", 200)).GetProperty("skills").GetArrayLength() == 0, "Fail-closed catalog");
    await Check(restricted, HttpMethod.Post, "/api/web/skills/comfyui.text-to-image/invoke", 403, "{\"prompt\":\"x\",\"idempotencyKey\":\"blocked\"}");
    await Check(restricted, HttpMethod.Post, $"/api/web/jobs/{jobId}/retry", 403);
    Stop();
    await Start(null);
    using var unconfigured = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    unconfigured.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-value");
    await Check(unconfigured, HttpMethod.Get, "/api/web/scene", 503);
    Stop();
    var independentBefore = File.ReadAllBytes(scenePath);
    var projectCanvasPath = Path.Combine(root, "project-canvas.json");
    var projectBytes = Encoding.UTF8.GetBytes("{\"Revision\":7,\"Canvas\":{\"Nodes\":[]}}");
    File.WriteAllBytes(projectCanvasPath, projectBytes);
    await Start(projectCanvas: projectCanvasPath);
    using var projectClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    projectClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-value");
    Assert((await Check(projectClient, HttpMethod.Get, "/api/web/scene", 503)).GetProperty("code").GetString() == "PROJECT_CANVAS_UNAVAILABLE", "Project canvas must not be represented by standalone JSON");
    Assert((await Check(projectClient, HttpMethod.Put, $"/api/web/records/{id}", 503, "{\"baseRevision\":5,\"title\":\"wrong\",\"content\":\"wrong\"}")).GetProperty("code").GetString() == "PROJECT_CANVAS_UNAVAILABLE", "Project canvas write must fail closed");
    using var projectAnonymous = new HttpClient { BaseAddress = projectClient.BaseAddress };
    await Check(projectAnonymous, HttpMethod.Get, "/api/web/scene", 401);
    Assert(independentBefore.SequenceEqual(File.ReadAllBytes(scenePath)) && projectBytes.SequenceEqual(File.ReadAllBytes(projectCanvasPath)), "Blocked project mode changed a file");
    Stop();
    // Real project authority: HTTP projection, desktop-format save, backup, rejection without byte changes.
    var projectRoot = Path.Combine(root, "real-project");
    var canvasDirectory = Path.Combine(projectRoot, "canvases");
    var entityDirectory = Path.Combine(projectRoot, "project");
    Directory.CreateDirectory(canvasDirectory);
    Directory.CreateDirectory(entityDirectory);
    File.WriteAllText(Path.Combine(projectRoot, "project.json"), "{\"Name\":\"HTTP test\"}");
    var realCanvas = Path.Combine(canvasDirectory, "main.json");
    var nodeId = Guid.NewGuid();
    var anotherId = Guid.NewGuid();
    var realEntities = Path.Combine(entityDirectory, "entities.json");
    File.WriteAllText(realEntities, "{\"formatVersion\":1,\"entities\":[]}");
    File.WriteAllText(realCanvas, JsonSerializer.Serialize(new { Title = "Project", Revision = 8, FormatVersion = 0,
        Canvas = new { Nodes = new object[] { new { Id = nodeId, Title = "Before", Content = "Body", Category = 0 },
            new { Id = anotherId, Title = "Sibling", Content = "Keep", Category = 0 } }, Edges = Array.Empty<object>(),
            Entities = Array.Empty<object>(), WorkTree = Array.Empty<object>() } }));
    var realBefore = File.ReadAllBytes(realCanvas);
    await Start(projectCanvas: realCanvas);
    using var realClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    realClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-value");
    var projected = await Check(realClient, HttpMethod.Get, "/api/web/scene", 200);
    Assert(projected.GetProperty("records")[0].GetProperty("recordId").GetString() == nodeId.ToString() &&
        projected.GetProperty("records")[1].GetProperty("recordId").GetString() == anotherId.ToString(), "Stable desktop node IDs");
    // 工作台的面包屑与画布标签要显示项目名与画布名（不是文件名），所以这两个名字必须投影出来。
    Assert(projected.GetProperty("projectName").GetString() == "HTTP test" &&
        projected.GetProperty("canvasTitle").GetString() == "Project", "Project and canvas names projected");
    Assert(realBefore.SequenceEqual(File.ReadAllBytes(realCanvas)), "GET migrated authoritative file on disk");
    SetClaims("skill.invoke,job.cancel");
    await WaitForEditClaim(realClient, nodeId.ToString(), allowed: false);
    Assert((await Check(realClient, HttpMethod.Put, $"/api/web/records/{nodeId}", 403,
        JsonSerializer.Serialize(new { baseRevision = projected.GetProperty("revision").GetInt64(), title = "Denied", content = "Denied" })))
        .GetProperty("code").GetString() == "CANVAS_EDIT_FORBIDDEN", "Project edit without claim");
    Assert((await Check(realClient, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64() ==
        projected.GetProperty("revision").GetInt64(), "Revoked project GET");
    Assert(realBefore.SequenceEqual(File.ReadAllBytes(realCanvas)) &&
        !Directory.Exists(Path.Combine(canvasDirectory, "backups")), "Revoked project PUT changed bytes or created backup");
    SetClaims("canvas.edit,skill.invoke,job.cancel");
    await WaitForEditClaim(realClient, nodeId.ToString(), allowed: true);
    var baseRevision = projected.GetProperty("revision").GetInt64();
    await Check(realClient, HttpMethod.Put, $"/api/web/records/{nodeId}", 409,
        JsonSerializer.Serialize(new { baseRevision = baseRevision + 1, title = "Bad", content = "Bad" }));
    await Check(realClient, HttpMethod.Put, $"/api/web/records/{anotherId}", 400, "{\"baseRevision\":0,\"title\":null}");
    await Check(realClient, HttpMethod.Put, $"/api/web/records/{Guid.NewGuid()}", 404,
        JsonSerializer.Serialize(new { baseRevision, title = "Bad", content = "Bad" }));
    Assert(realBefore.SequenceEqual(File.ReadAllBytes(realCanvas)), "Rejected project writes changed bytes");
    var updateProject = await Check(realClient, HttpMethod.Put, $"/api/web/records/{nodeId}", 200,
        JsonSerializer.Serialize(new { baseRevision, title = "After", content = "Edited" }));
    Assert(updateProject.GetProperty("record").GetProperty("recordId").GetString() == nodeId.ToString(), "Project update identity");
    using (var document = JsonDocument.Parse(File.ReadAllText(realCanvas)))
    {
        Assert(document.RootElement.GetProperty("FormatVersion").GetInt32() == 1 &&
            document.RootElement.GetProperty("Canvas").GetProperty("Nodes")[0].GetProperty("Title").GetString() == "After" &&
            document.RootElement.GetProperty("Canvas").GetProperty("Nodes")[1].GetProperty("Content").GetString() == "Keep", "Desktop authoritative format persisted");
    }
    Assert(Directory.GetFiles(Path.Combine(canvasDirectory, "backups"), "*.json").Length > 0, "Desktop backup missing");
    Assert(independentBefore.SequenceEqual(File.ReadAllBytes(scenePath)), "Project request fell back to standalone");
    var savedBytes = File.ReadAllBytes(realCanvas);
    await Check(realClient, HttpMethod.Put, $"/api/web/records/{nodeId}", 409,
        JsonSerializer.Serialize(new { baseRevision, title = "Stale", content = "Stale" }));
    Assert(savedBytes.SequenceEqual(File.ReadAllBytes(realCanvas)), "Stale project write changed bytes");
    var lockPath = realCanvas + ".web.lock";
    using (var heldLease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
        Assert((await Check(realClient, HttpMethod.Put, $"/api/web/records/{nodeId}", 503,
            JsonSerializer.Serialize(new { baseRevision = updateProject.GetProperty("revision").GetInt64(), title = "Blocked", content = "Blocked" })))
            .GetProperty("code").GetString() == "PROJECT_CANVAS_WRITE_FAILED", "Shared lease not respected");
        Assert(savedBytes.SequenceEqual(File.ReadAllBytes(realCanvas)), "Locked save changed bytes");
    }
    var currentRevision = updateProject.GetProperty("revision").GetInt64();
    File.WriteAllText(realEntities, "not JSON");
    Assert((await Check(realClient, HttpMethod.Get, "/api/web/scene", 503)).GetProperty("code").GetString() == "PROJECT_CANVAS_READ_FAILED", "Corrupt library read not rejected");
    await Check(realClient, HttpMethod.Put, $"/api/web/records/{nodeId}", 503,
        JsonSerializer.Serialize(new { baseRevision = currentRevision, title = "Blocked", content = "Blocked" }));
    Assert(savedBytes.SequenceEqual(File.ReadAllBytes(realCanvas)), "Corrupt library write changed bytes");
    File.WriteAllText(realEntities, "{\"formatVersion\":1,\"entities\":[]}");
    var managedEntity = Guid.NewGuid();
    var managedVariant = Guid.NewGuid();
    var managedVersion = Guid.NewGuid();
    var managedSnapshot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(realCanvas))!;
    managedSnapshot["Canvas"]!["Entities"] = new System.Text.Json.Nodes.JsonArray(
        System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { Id = managedEntity, ManagedByProject = true,
            Variants = new[] { new { Id = managedVariant, Versions = new[] { new { Id = managedVersion } } } } })));
    managedSnapshot["Canvas"]!["Nodes"]![0]!["References"] = new System.Text.Json.Nodes.JsonArray(
        System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { EntityId = managedEntity, VariantId = managedVariant, VariantVersionId = managedVersion })));
    File.WriteAllText(realCanvas, managedSnapshot.ToJsonString());
    var managedBytes = File.ReadAllBytes(realCanvas);
    Assert((await Check(realClient, HttpMethod.Get, "/api/web/scene", 503)).GetProperty("code").GetString() == "PROJECT_CANVAS_READ_FAILED", "Missing managed authority accepted");
    File.WriteAllText(realEntities, JsonSerializer.Serialize(new { formatVersion = 1, entities = new[] {
        new { id = managedEntity, managedByProject = true, variants = new[] { new { id = managedVariant, versions = new[] { new { id = managedVersion } } } } } } }));
    var managedRead = await Check(realClient, HttpMethod.Get, "/api/web/scene", 200);
    var managedRevision = managedRead.GetProperty("revision").GetInt64();
    File.WriteAllText(realEntities, JsonSerializer.Serialize(new { formatVersion = 1, entities = new[] {
        new { id = managedEntity, managedByProject = true, variants = Array.Empty<object>() } } }));
    Assert((await Check(realClient, HttpMethod.Get, "/api/web/scene", 503)).GetProperty("code").GetString() == "PROJECT_CANVAS_READ_FAILED", "Missing library variant accepted");
    await Check(realClient, HttpMethod.Put, $"/api/web/records/{nodeId}", 503,
        JsonSerializer.Serialize(new { baseRevision = managedRevision, title = "Blocked", content = "Blocked" }));
    Assert(managedBytes.SequenceEqual(File.ReadAllBytes(realCanvas)), "Invalid authority changed canvas");
    File.WriteAllText(realEntities, JsonSerializer.Serialize(new { formatVersion = 1, entities = new[] {
        new { id = managedEntity, managedByProject = true, variants = new[] { new { id = managedVariant, versions = Array.Empty<object>() } } } } }));
    Assert((await Check(realClient, HttpMethod.Get, "/api/web/scene", 503)).GetProperty("code").GetString() == "PROJECT_CANVAS_READ_FAILED", "Missing library version accepted");
    File.WriteAllText(realEntities, JsonSerializer.Serialize(new { formatVersion = 1, entities = new[] {
        new { id = managedEntity, managedByProject = true, variants = new[] { new { id = managedVariant, versions = new[] { new { id = managedVersion } } } } } } }));
    managedSnapshot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(realCanvas))!;
    managedSnapshot["Canvas"]!["WorkTree"] = new System.Text.Json.Nodes.JsonArray(
        System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { Id = Guid.NewGuid(), SourceEntityId = managedEntity,
            SourceVariantId = managedVariant, SourceVersionId = managedVersion })));
    File.WriteAllText(realCanvas, managedSnapshot.ToJsonString());
    managedRevision = (await Check(realClient, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64();
    File.WriteAllText(realEntities, JsonSerializer.Serialize(new { formatVersion = 1, entities = new[] {
        new { id = managedEntity, managedByProject = true, variants = new[] { new { id = managedVariant, versions = Array.Empty<object>() } } } } }));
    Assert((await Check(realClient, HttpMethod.Get, "/api/web/scene", 503)).GetProperty("code").GetString() == "PROJECT_CANVAS_READ_FAILED", "Work-tree library version not checked");
    File.WriteAllText(realEntities, JsonSerializer.Serialize(new { formatVersion = 1, entities = new[] {
        new { id = managedEntity, managedByProject = true, variants = new[] { new { id = managedVariant, versions = new[] { new { id = managedVersion } } } } } } }));
    var managedUpdate = await Check(realClient, HttpMethod.Put, $"/api/web/records/{nodeId}", 200,
        JsonSerializer.Serialize(new { baseRevision = managedRevision, title = "Managed", content = "Saved" }));
    Assert(managedUpdate.GetProperty("revision").GetInt64() == (await Check(realClient, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64(), "Committed revision differs from disk");
    Assert(Directory.GetFiles(Path.Combine(canvasDirectory, "backups"), "*.json").Length >= 2, "Managed canvas backup missing");
    // Simulate a desktop save that keeps its own Revision unchanged: byte fingerprint still conflicts.
    File.WriteAllText(realCanvas, File.ReadAllText(realCanvas).Replace("\"Keep\"", "\"Desktop edit\""));
    var desktopBytes = File.ReadAllBytes(realCanvas);
    await Check(realClient, HttpMethod.Put, $"/api/web/records/{nodeId}", 409,
        JsonSerializer.Serialize(new { baseRevision = updateProject.GetProperty("revision").GetInt64(), title = "Stale", content = "Stale" }));
    Assert(desktopBytes.SequenceEqual(File.ReadAllBytes(realCanvas)), "Desktop conflict changed bytes");
    using (var futureJson = JsonDocument.Parse(File.ReadAllText(realCanvas)))
    {
        var futureObject = System.Text.Json.Nodes.JsonNode.Parse(futureJson.RootElement.GetRawText())!;
        futureObject["FormatVersion"] = 999;
        File.WriteAllText(realCanvas, futureObject.ToJsonString());
    }
    var futureBytes = File.ReadAllBytes(realCanvas);
    var futureScene = await Check(realClient, HttpMethod.Get, "/api/web/scene", 200);
    Assert(futureScene.GetProperty("readOnly").GetBoolean(), "Future format must be read-only");
    Assert((await Check(realClient, HttpMethod.Put, $"/api/web/records/{nodeId}", 409,
        JsonSerializer.Serialize(new { baseRevision = futureScene.GetProperty("revision").GetInt64(), title = "No", content = "No" })))
        .GetProperty("code").GetString() == "CANVAS_READ_ONLY", "Future format write not blocked");
    Assert(futureBytes.SequenceEqual(File.ReadAllBytes(realCanvas)), "Future format failure changed bytes");
    File.WriteAllText(realCanvas, File.ReadAllText(realCanvas).Replace(nodeId.ToString(), anotherId.ToString()));
    var invalidBytes = File.ReadAllBytes(realCanvas);
    var invalidScene = await Check(realClient, HttpMethod.Get, "/api/web/scene", 200);
    Assert(invalidScene.GetProperty("readOnly").GetBoolean(), "Invalid identity must be read-only");
    await Check(realClient, HttpMethod.Put, $"/api/web/records/{anotherId}", 409,
        JsonSerializer.Serialize(new { baseRevision = invalidScene.GetProperty("revision").GetInt64(), title = "No", content = "No" }));
    Assert(invalidBytes.SequenceEqual(File.ReadAllBytes(realCanvas)), "Invalid identity failure changed bytes");
    Stop();
    await Start(standalone: false);
    using var noMode = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    noMode.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-value");
    Assert((await Check(noMode, HttpMethod.Get, "/api/web/scene", 503)).GetProperty("code").GetString() == "SCENE_MODE_NOT_CONFIGURED", "Missing explicit standalone mode must fail closed");
    await Check(noMode, HttpMethod.Put, $"/api/web/records/{id}", 503, "{\"baseRevision\":5,\"title\":\"wrong\",\"content\":\"wrong\"}");
    Assert(independentBefore.SequenceEqual(File.ReadAllBytes(scenePath)), "Disabled standalone mode changed scene");

    // ---------- 账号与会话：第一个用户 = 管理员 ----------
    Stop();
    var userDatabase = Path.Combine(root, "users-auth.db");
    foreach (var leftover in new[] { userDatabase, userDatabase + "-wal", userDatabase + "-shm" }) if (File.Exists(leftover)) File.Delete(leftover);
    await Start(userDatabase: userDatabase);

    // 带 cookie 容器的客户端：**不设任何 Bearer**，走的就是浏览器那条路。
    // 可以传一个已有的 cookie 罐进来——重启之后端口会变，旧客户端指向的是死地址，
    // 这时要用同一个罐建新客户端（cookie 不区分端口，所以能接着用）。
    HttpClient CookieClient(CookieContainer? jar = null) =>
        new(new HttpClientHandler { CookieContainer = jar ?? new CookieContainer(), UseCookies = true })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    string Snapshot() => File.ReadAllText(scenePath);

    using var admin = CookieClient();
    var freshState = await Check(admin, HttpMethod.Get, "/api/auth/state", 200);
    Assert(!freshState.GetProperty("initialized").GetBoolean() &&
        freshState.GetProperty("user").ValueKind == JsonValueKind.Null, "空库必须如实报「还没有账号」");
    Assert(freshState.GetProperty("passwordMinLength").GetInt32() >= 8, "口令长度下限没告诉前端");

    // 建号前打场景接口：应当是「未登录」，不是「令牌无效」。
    Assert((await Check(admin, HttpMethod.Get, "/api/web/scene", 401)).GetProperty("code").GetString() == "UNAUTHORIZED", "未登录的读应当回 401");

    await Check(admin, HttpMethod.Post, "/api/auth/setup", 400, "{\"username\":\"lin\",\"password\":\"short\"}");
    Assert((await Check(admin, HttpMethod.Post, "/api/auth/setup", 400, "{\"username\":\"林晚\",\"password\":\"longenough\"}")).GetProperty("code").GetString() == "INVALID_USERNAME", "非法用户名要挡住");
    var created = await Check(admin, HttpMethod.Post, "/api/auth/setup", 200, "{\"username\":\"lin\",\"password\":\"longenough\",\"displayName\":\"林晚\"}");
    Assert(created.GetProperty("user").GetProperty("role").GetString() == "Admin", "第一个用户必须是管理员");
    var adminId = created.GetProperty("user").GetProperty("id").GetGuid();

    // 再建一次：必须 409，不能靠「先到先得」被第二个人抢成管理员。
    await Check(admin, HttpMethod.Post, "/api/auth/setup", 409, "{\"username\":\"other\",\"password\":\"longenough\"}");

    // 会话 cookie 本身就是身份：不带 Bearer 也能读、也能改。
    var sceneBySession = await Check(admin, HttpMethod.Get, "/api/web/scene", 200);
    var sessionRevision = sceneBySession.GetProperty("revision").GetInt64();
    using var noCookie = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    Assert((await Check(noCookie, HttpMethod.Get, "/api/web/scene", 401)).GetProperty("code").GetString() == "UNAUTHORIZED", "没有 cookie 的读必须被拒");
    var beforeSession = Snapshot();
    await Check(admin, HttpMethod.Put, $"/api/web/records/{id}", 200,
        JsonSerializer.Serialize(new { baseRevision = sessionRevision, title = "BySession", content = "BySession" }));
    Assert(beforeSession != Snapshot(), "管理员用会话改画布应当真的落盘");

    // 管理员建一个只读账号：它能读，改不了。
    var viewerCreated = await Check(admin, HttpMethod.Post, "/api/auth/users", 200, "{\"username\":\"viewer1\",\"password\":\"longenough\",\"role\":\"Viewer\"}");
    var viewerId = viewerCreated.GetProperty("user").GetProperty("id").GetGuid();
    using var viewer = CookieClient();
    await Check(viewer, HttpMethod.Post, "/api/auth/login", 401, "{\"username\":\"viewer1\",\"password\":\"wrong-password\"}");
    var viewerLogin = await Check(viewer, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"viewer1\",\"password\":\"longenough\"}");
    Assert(viewerLogin.GetProperty("user").GetProperty("role").GetString() == "Viewer", "建号时的角色要生效");
    var viewerScene = await Check(viewer, HttpMethod.Get, "/api/web/scene", 200);
    var viewerRevision = viewerScene.GetProperty("revision").GetInt64();
    Assert((await Check(viewer, HttpMethod.Put, $"/api/web/records/{id}", 403,
        JsonSerializer.Serialize(new { baseRevision = viewerRevision, title = "Nope", content = "Nope" })))
        .GetProperty("code").GetString() == "CANVAS_EDIT_FORBIDDEN", "只读角色的写入必须被拒");
    // 管用户是管理员专属，只读账号连列表都看不到。
    Assert((await Check(viewer, HttpMethod.Get, "/api/auth/users", 403)).GetProperty("code").GetString() == "ADMIN_REQUIRED", "非管理员不该能列账号");
    Assert((await Check(noCookie, HttpMethod.Get, "/api/auth/users", 401)).GetProperty("code").GetString() == "UNAUTHORIZED", "未登录不该能列账号");

    // 最后一个管理员不能被降级：否则没人能再管账号。
    Assert((await Check(admin, HttpMethod.Patch, $"/api/auth/users/{adminId}", 409, "{\"role\":\"Viewer\"}"))
        .GetProperty("code").GetString() == "LAST_ADMIN", "唯一管理员不该能降级");
    Assert((await Check(admin, HttpMethod.Delete, $"/api/auth/users/{adminId}", 409)).GetProperty("code").GetString() == "LAST_ADMIN", "唯一管理员不该能删除");

    // 停用一个账号：它手里的会话要**立刻**失效，不能等 cookie 过期。
    await Check(admin, HttpMethod.Patch, $"/api/auth/users/{viewerId}", 200, "{\"disabled\":true}");
    Assert((await Check(viewer, HttpMethod.Get, "/api/web/scene", 401)).GetProperty("code").GetString() == "UNAUTHORIZED", "被停用账号的会话必须立刻失效");
    Assert((await Check(viewer, HttpMethod.Post, "/api/auth/login", 403, "{\"username\":\"viewer1\",\"password\":\"longenough\"}"))
        .GetProperty("code").GetString() == "ACCOUNT_DISABLED", "被停用的账号登录要如实说停用");
    await Check(admin, HttpMethod.Patch, $"/api/auth/users/{viewerId}", 200, "{\"disabled\":false}");

    // 会话存在库里：进程重启之后同一张 cookie 还能用。
    var survivorJar = new CookieContainer();
    var survivor = CookieClient(survivorJar);
    await Check(survivor, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"viewer1\",\"password\":\"longenough\"}");
    Stop();
    await Start(userDatabase: userDatabase);
    var survivorAfterRestart = CookieClient(survivorJar);
    Assert((await Check(survivorAfterRestart, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64() > 0, "会话应当跨重启有效");

    // 自己改口令：旧口令错了要拒；改成功之后旧会话失效，新口令能登录。
    using var changer = CookieClient();
    await Check(changer, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"viewer1\",\"password\":\"longenough\"}");
    Assert((await Check(changer, HttpMethod.Post, "/api/auth/password", 401, "{\"currentPassword\":\"wrong\",\"newPassword\":\"brand-new-pass\"}"))
        .GetProperty("code").GetString() == "INVALID_CREDENTIALS", "旧口令不对要拒");
    await Check(changer, HttpMethod.Post, "/api/auth/password", 200, "{\"currentPassword\":\"longenough\",\"newPassword\":\"brand-new-pass\"}");
    Assert((await Check(changer, HttpMethod.Get, "/api/web/scene", 401)).GetProperty("code").GetString() == "UNAUTHORIZED", "改完口令旧会话必须失效");
    using var relogin = CookieClient();
    await Check(relogin, HttpMethod.Post, "/api/auth/login", 401, "{\"username\":\"viewer1\",\"password\":\"longenough\"}");
    await Check(relogin, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"viewer1\",\"password\":\"brand-new-pass\"}");

    // 退出登录之后 cookie 立刻作废。
    await Check(relogin, HttpMethod.Post, "/api/auth/logout", 200);
    Assert((await Check(relogin, HttpMethod.Get, "/api/web/scene", 401)).GetProperty("code").GetString() == "UNAUTHORIZED", "退出后不该还能读");

    // 部署要求初始化令牌时，没带令牌谁都建不了号——这是给公网容器留的那道门。
    Stop();
    var tokenDatabase = Path.Combine(root, "users-setup-token.db");
    if (File.Exists(tokenDatabase)) File.Delete(tokenDatabase);
    await Start(userDatabase: tokenDatabase, setupToken: "deploy-secret");
    using var guarded = CookieClient();
    Assert((await Check(guarded, HttpMethod.Post, "/api/auth/setup", 403, "{\"username\":\"lin\",\"password\":\"longenough\"}"))
        .GetProperty("code").GetString() == "SETUP_TOKEN_REQUIRED", "配了初始化令牌就必须校验");
    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/setup"))
    {
        request.Content = new StringContent("{\"username\":\"lin\",\"password\":\"longenough\"}", Encoding.UTF8, "application/json");
        request.Headers.Add("X-Setup-Token", "deploy-secret");
        using var response = await guarded.SendAsync(request);
        Assert((int)response.StatusCode == 200, "带了正确的初始化令牌应当能建号：" + await response.Content.ReadAsStringAsync());
    }

    // 但**空字符串的初始化令牌必须按「没配」算**：docker compose 里写 ${VAR:-} 时，
    // 变量没设传进来的就是一个空串。若当成「配了令牌」，界面会多出一个谁也填不出的
    // 初始化令牌输入框，首次建号直接卡死——这条是真机上跑容器时踩到的。
    Stop();
    var blankTokenDatabase = Path.Combine(root, "users-blank-token.db");
    if (File.Exists(blankTokenDatabase)) File.Delete(blankTokenDatabase);
    await Start(userDatabase: blankTokenDatabase, setupToken: "");
    using var blank = CookieClient();
    Assert(!(await Check(blank, HttpMethod.Get, "/api/auth/state", 200)).GetProperty("setupTokenRequired").GetBoolean(),
        "空令牌不该被当成已配置");
    Assert((await Check(blank, HttpMethod.Post, "/api/auth/setup", 200, "{\"username\":\"lin\",\"password\":\"longenough\"}"))
        .GetProperty("user").GetProperty("role").GetString() == "Admin", "空令牌下首次建号应当直接成功");
    // ---------- 编辑锁：谁在编辑、粒度、冲突、心跳、过期、强制接管 ----------
    // 锁是**会话状态**，不是文档内容：挨着画布放在 <画布>.edits.json，不写进画布文件。
    // 所以这里也断言它**不碰画布字节**。
    Stop();
    var leaseDatabase = Path.Combine(root, "users-lease.db");
    foreach (var leftover in new[] { leaseDatabase, leaseDatabase + "-wal", leaseDatabase + "-shm" })
        if (File.Exists(leftover)) File.Delete(leftover);
    var leasePath = scenePath + ".edits.json";
    if (File.Exists(leasePath)) File.Delete(leasePath);
    await Start(userDatabase: leaseDatabase);

    using var boss = CookieClient();
    await Check(boss, HttpMethod.Post, "/api/auth/setup", 200, "{\"username\":\"lin\",\"password\":\"longenough\",\"displayName\":\"林晚\"}");
    await Check(boss, HttpMethod.Post, "/api/auth/users", 200, "{\"username\":\"chenmo\",\"password\":\"longenough\",\"displayName\":\"陈默\",\"role\":\"Editor\"}");
    await Check(boss, HttpMethod.Post, "/api/auth/users", 200, "{\"username\":\"suli\",\"password\":\"longenough\",\"displayName\":\"苏黎\",\"role\":\"Viewer\"}");
    using var mate = CookieClient();
    await Check(mate, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"chenmo\",\"password\":\"longenough\"}");
    using var reader = CookieClient();
    await Check(reader, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"suli\",\"password\":\"longenough\"}");
    using var ghost = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var nodeA = Guid.Parse(id);
    var nodeB = Guid.Parse(other);
    var canvasBeforeLock = File.ReadAllBytes(scenePath);
    string Lease(string scope, Guid? target, string? client = null, bool force = false) =>
        JsonSerializer.Serialize(new { scope, targetId = target, client, force });

    // 未登录连锁都查不了；只读账号能看不能占（否则只读也能把人挡在外面）。
    Assert((await Check(ghost, HttpMethod.Get, "/api/web/edits", 401)).GetProperty("code").GetString() == "UNAUTHORIZED", "未登录不该能查锁");
    Assert((await Check(reader, HttpMethod.Get, "/api/web/edits", 200)).GetProperty("leases").GetArrayLength() == 0, "一开始不该有锁");
    Assert((await Check(reader, HttpMethod.Post, "/api/web/edits", 403, Lease("node", nodeA)))
        .GetProperty("code").GetString() == "CANVAS_EDIT_FORBIDDEN", "只读账号不该能占锁");

    // 申请节点锁：来源端由调用方声明，只影响「某某（桌面端）」这种文案。
    var held = await Check(mate, HttpMethod.Post, "/api/web/edits", 200, Lease("node", nodeA, "desktop"));
    var heldA = held.GetProperty("lease").GetProperty("leaseId").GetGuid();
    Assert(held.GetProperty("lease").GetProperty("scope").GetString() == "node" &&
        held.GetProperty("lease").GetProperty("targetId").GetGuid() == nodeA, "节点锁的范围要如实回给前端");

    // 同一目标重复申请是幂等的：连点两下不该把自己挡住。
    Assert((await Check(mate, HttpMethod.Post, "/api/web/edits", 200, Lease("node", nodeA)))
        .GetProperty("lease").GetProperty("leaseId").GetGuid() == heldA, "重复申请应当幂等");

    // 别人抢同一节点：409，并且必须告诉他等谁——否则界面只能说「被占用」，用户不知道该找谁。
    var blocked = await Check(boss, HttpMethod.Post, "/api/web/edits", 409, Lease("node", nodeA));
    Assert(blocked.GetProperty("code").GetString() == "EDIT_CONFLICT", "抢同一节点要回冲突");
    Assert(blocked.GetProperty("holder").GetProperty("displayName").GetString() == "陈默" &&
        blocked.GetProperty("holder").GetProperty("client").GetString() == "desktop", "冲突要带上持有者与来源端：" + blocked.GetRawText());
    Assert(blocked.GetProperty("message").GetString()!.Contains("陈默（桌面端）", StringComparison.Ordinal), "冲突文案要能直接显示给用户");

    // 不同节点可以并存；但有人持节点锁时，整棵树锁应当被挡。
    var heldB = (await Check(boss, HttpMethod.Post, "/api/web/edits", 200, Lease("node", nodeB))).GetProperty("lease").GetProperty("leaseId").GetGuid();
    Assert((await Check(mate, HttpMethod.Post, "/api/web/edits", 409, Lease("tree", null))).GetProperty("code").GetString() == "EDIT_CONFLICT", "节点锁应当挡住整棵树锁");

    // 只读账号也要能看到「谁在编辑」，这是协作里最要紧的一条。
    var visible = await Check(reader, HttpMethod.Get, "/api/web/edits", 200);
    Assert(visible.GetProperty("leases").GetArrayLength() == 2, "两个节点锁都该列出来");
    Assert(visible.GetProperty("leases")[0].GetProperty("displayName").GetString() is "陈默" or "林晚", "锁要带显示名");
    Assert(visible.GetProperty("lifetimeSeconds").GetInt32() > 0, "要把有效期告诉前端，心跳才有个准");

    // 心跳续期：自己续得上，别人续不了、也释放不了。
    Assert((await Check(boss, HttpMethod.Put, $"/api/web/edits/{heldB}", 200)).GetProperty("lease").GetProperty("leaseId").GetGuid() == heldB, "持有者应当能续期");
    Assert((await Check(mate, HttpMethod.Put, $"/api/web/edits/{heldB}", 403)).GetProperty("code").GetString() == "EDIT_LEASE_NOT_HOLDER", "别人不该能续别人的锁");
    Assert((await Check(mate, HttpMethod.Delete, $"/api/web/edits/{heldB}", 403)).GetProperty("code").GetString() == "EDIT_LEASE_NOT_HOLDER", "别人不该能释放别人的锁");

    // 释放之后别人能接手。先把外部持有的锁清空，才好单独验整棵树锁。
    await Check(mate, HttpMethod.Delete, $"/api/web/edits/{heldA}", 200);
    var takenOver = (await Check(boss, HttpMethod.Post, "/api/web/edits", 200, Lease("node", nodeA))).GetProperty("lease").GetProperty("leaseId").GetGuid();
    await Check(boss, HttpMethod.Delete, $"/api/web/edits/{heldB}", 200);
    await Check(boss, HttpMethod.Delete, $"/api/web/edits/{takenOver}", 200);

    // 整棵树锁：挡住一切。顺带验证「自己原有的节点锁被整棵树锁吸收」——否则会留下没人续期的僵尸锁。
    var mateB = (await Check(mate, HttpMethod.Post, "/api/web/edits", 200, Lease("node", nodeB))).GetProperty("lease").GetProperty("leaseId").GetGuid();
    var treeLease = (await Check(mate, HttpMethod.Post, "/api/web/edits", 200, Lease("tree", null))).GetProperty("lease").GetProperty("leaseId").GetGuid();
    Assert((await Check(mate, HttpMethod.Get, "/api/web/edits", 200)).GetProperty("leases").GetArrayLength() == 1, "整棵树锁应当吸收掉自己原有的节点锁");
    Assert((await Check(mate, HttpMethod.Put, $"/api/web/edits/{mateB}", 404)).GetProperty("code").GetString() == "EDIT_LEASE_NOT_FOUND", "被吸收的节点锁不该还能续期");
    Assert((await Check(boss, HttpMethod.Post, "/api/web/edits", 409, Lease("node", nodeA))).GetProperty("code").GetString() == "EDIT_CONFLICT", "整棵树锁应当挡住节点锁");
    Assert((await Check(boss, HttpMethod.Post, "/api/web/edits", 409, Lease("tree", null))).GetProperty("code").GetString() == "EDIT_CONFLICT", "整棵树锁不该被别人重复拿到");
    Assert((await Check(mate, HttpMethod.Put, $"/api/web/edits/{treeLease}", 200)).GetProperty("lease").GetProperty("scope").GetString() == "tree", "树锁也要能续期");

    // 非法输入要挡住，不能猜。
    await Check(mate, HttpMethod.Post, "/api/web/edits", 400, "{}");
    await Check(mate, HttpMethod.Post, "/api/web/edits", 400, Lease("node", null));
    await Check(mate, HttpMethod.Post, "/api/web/edits", 400, Lease("tree", nodeA));
    await Check(mate, HttpMethod.Post, "/api/web/edits", 400, Lease("node", nodeA, "phone"));
    Assert((await Check(mate, HttpMethod.Put, "/api/web/edits/not-a-guid", 404)).GetProperty("code").GetString() == "EDIT_LEASE_NOT_FOUND", "认不出的锁 ID 当成不存在");
    await Check(mate, HttpMethod.Delete, "/api/web/edits/not-a-guid", 404);

    // 强制接管是管理员专属，而且要显式带 force：不提供「悄悄踢掉别人」的默认行为。
    Assert((await Check(mate, HttpMethod.Post, "/api/web/edits", 403, Lease("node", nodeB, force: true))).GetProperty("code").GetString() == "ADMIN_REQUIRED", "非管理员不该能强制接管");
    Assert((await Check(boss, HttpMethod.Delete, $"/api/web/edits/{treeLease}", 403)).GetProperty("code").GetString() == "EDIT_LEASE_NOT_HOLDER", "非持有者不该能释放别人的锁");
    var taken = await Check(boss, HttpMethod.Post, "/api/web/edits", 200, Lease("node", nodeB, force: true));
    Assert(taken.GetProperty("displaced").GetArrayLength() == 1 &&
        taken.GetProperty("displaced")[0].GetProperty("displayName").GetString() == "陈默", "强制接管要如实说出顶掉了谁");
    Assert((await Check(mate, HttpMethod.Put, $"/api/web/edits/{treeLease}", 404)).GetProperty("code").GetString() == "EDIT_LEASE_NOT_FOUND", "被顶掉之后续期应当说「不在了」，让界面停下编辑");
    var bossNode = taken.GetProperty("lease").GetProperty("leaseId").GetGuid();
    Assert((await Check(mate, HttpMethod.Delete, $"/api/web/edits/{bossNode}?force=true", 403)).GetProperty("code").GetString() == "ADMIN_REQUIRED", "非管理员不该能强制释放");
    await Check(boss, HttpMethod.Delete, $"/api/web/edits/{bossNode}?force=true", 200);

    // 锁不写进画布：一路折腾下来画布字节必须一个都没变。
    Assert(canvasBeforeLock.SequenceEqual(File.ReadAllBytes(scenePath)), "编辑锁不该改动画布字节");
    Assert(File.Exists(leasePath), "锁应当落在画布旁边");

    // 锁存在文件里：服务重启之后仍然生效（TTL 内），不会因为一次重启把所有人放进来。
    var leaseSurvivor = (await Check(mate, HttpMethod.Post, "/api/web/edits", 200, Lease("node", nodeA))).GetProperty("lease").GetProperty("leaseId").GetGuid();
    Stop();
    await Start(userDatabase: leaseDatabase, leaseSeconds: 120);
    using var restored = CookieClient();
    await Check(restored, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"lin\",\"password\":\"longenough\"}");
    var acrossRestart = await Check(restored, HttpMethod.Get, "/api/web/edits", 200);
    Assert(acrossRestart.GetProperty("leases").GetArrayLength() == 1 &&
        acrossRestart.GetProperty("leases")[0].GetProperty("displayName").GetString() == "陈默", "锁应当跨重启有效，且归属不变");
    Assert((await Check(restored, HttpMethod.Put, $"/api/web/edits/{leaseSurvivor}", 403)).GetProperty("code").GetString() == "EDIT_LEASE_NOT_HOLDER", "重启后别人仍然不能续别人的锁");

    // 锁文件损坏时按「没有锁」处理：宁可短暂放两个人进来，也不要让整张画布永远无法编辑。
    File.WriteAllText(leasePath, "{ this is not json");
    Assert((await Check(restored, HttpMethod.Get, "/api/web/edits", 200)).GetProperty("leases").GetArrayLength() == 0, "损坏的锁文件按没有锁处理");
    var healed = (await Check(restored, HttpMethod.Post, "/api/web/edits", 200, Lease("node", nodeA))).GetProperty("lease").GetProperty("leaseId").GetGuid();
    Assert((await Check(restored, HttpMethod.Get, "/api/web/edits", 200)).GetProperty("leases").GetArrayLength() == 1, "损坏之后应当能重新申请并自愈");
    await Check(restored, HttpMethod.Delete, $"/api/web/edits/{healed}", 200);

    // 过期：持有者续期要如实说「已过期」（不能硬写），别人则应当能直接接手。
    Stop();
    await Start(userDatabase: leaseDatabase, leaseSeconds: 2);
    using var shortLived = CookieClient();
    await Check(shortLived, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"chenmo\",\"password\":\"longenough\"}");
    var expiring = (await Check(shortLived, HttpMethod.Post, "/api/web/edits", 200, Lease("node", nodeA))).GetProperty("lease").GetProperty("leaseId").GetGuid();
    await Task.Delay(2500);
    Assert((await Check(shortLived, HttpMethod.Put, $"/api/web/edits/{expiring}", 410)).GetProperty("code").GetString() == "EDIT_LEASE_EXPIRED", "过期之后续期要说「已过期」，而不是硬写");
    using var inheritor = CookieClient();
    await Check(inheritor, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"lin\",\"password\":\"longenough\"}");
    var inherited = (await Check(inheritor, HttpMethod.Post, "/api/web/edits", 200, Lease("node", nodeA))).GetProperty("lease").GetProperty("leaseId").GetGuid();
    Assert(File.ReadAllText(leasePath).Contains(inherited.ToString(), StringComparison.Ordinal), "锁要真的写进锁文件，重启后才能在");
    await Check(inheritor, HttpMethod.Delete, $"/api/web/edits/{inherited}", 200);

    // 场景模式不可用时，锁也跟着不可用——两边解出来的必须是同一张画布。
    Stop();
    await Start(standalone: false);
    using var noModeLease = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    noModeLease.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-value");
    Assert((await Check(noModeLease, HttpMethod.Get, "/api/web/edits", 503)).GetProperty("code").GetString() == "SCENE_MODE_NOT_CONFIGURED", "没有画布时锁接口也要如实报没配");
    Assert((await Check(noModeLease, HttpMethod.Post, "/api/web/layout/plan", 503, "{}")).GetProperty("code").GetString() == "SCENE_MODE_NOT_CONFIGURED", "没有画布时整理布局也要如实报没配");

    // ---------- 整理布局：服务端直接用桌面端那份泳道引擎，写入口与改文字是同一个 ----------
    Stop();
    var layoutRoot = Path.Combine(root, "layout-project");
    var layoutCanvases = Path.Combine(layoutRoot, "canvases");
    var layoutEntities = Path.Combine(layoutRoot, "project");
    Directory.CreateDirectory(layoutCanvases);
    Directory.CreateDirectory(layoutEntities);
    File.WriteAllText(Path.Combine(layoutRoot, "project.json"), "{\"Name\":\"Layout test\"}");
    File.WriteAllText(Path.Combine(layoutEntities, "entities.json"), "{\"formatVersion\":1,\"entities\":[]}");
    var layoutCanvas = Path.Combine(layoutCanvases, "main.json");
    var chapterNode = Guid.NewGuid();
    var storyboardNode = Guid.NewGuid();
    var productNode = Guid.NewGuid();
    var planningNode = Guid.NewGuid();
    var characterNode = Guid.NewGuid();
    var chapterItem = Guid.NewGuid();
    // 故意摆得又散又远：整理**必须**真的算出改动，否则下面那些断言等于没测。
    File.WriteAllText(layoutCanvas, JsonSerializer.Serialize(new
    {
        Title = "排版测试",
        Revision = 3,
        FormatVersion = 0,
        Canvas = new
        {
            Nodes = new object[]
            {
                new { Id = chapterNode, Title = "第一章", Category = 8, Content = "", X = 1500, Y = 900, WorkTreeItemId = chapterItem },
                new { Id = storyboardNode, Title = "分镜一", Category = 3, Content = "", X = 1600, Y = 1000, WorkTreeItemId = chapterItem },
                new { Id = productNode, Title = "成品一", Category = 5, Content = "", X = 1700, Y = 1100, ParentNodeId = (Guid?)storyboardNode },
                new { Id = planningNode, Title = "总企划", Category = 6, Content = "", X = 1800, Y = 1200 },
                new { Id = characterNode, Title = "林晚", Category = 1, Content = "", X = 1900, Y = 1300 }
            },
            Edges = Array.Empty<object>(),
            Entities = Array.Empty<object>(),
            WorkTree = new object[]
            {
                // Kind 必须显式写成章节：引擎按 WorkTreeKind.Chapter 找章节条目，
                // 少了它这份工作树就只是一堆「能力」条目，泳道一条也建不出来。
                new { Id = chapterItem, Kind = (int)YEEYEEYEE.Desktop.WorkTreeKind.Chapter, Name = "第一章", Order = 0, Prompt = "" }
            }
        }
    }));
    var layoutDatabase = Path.Combine(root, "users-layout.db");
    foreach (var leftover in new[] { layoutDatabase, layoutDatabase + "-wal", layoutDatabase + "-shm" })
        if (File.Exists(leftover)) File.Delete(leftover);
    await Start(projectCanvas: layoutCanvas, userDatabase: layoutDatabase);

    using var arranger = CookieClient();
    await Check(arranger, HttpMethod.Post, "/api/auth/setup", 200, "{\"username\":\"lin\",\"password\":\"longenough\",\"displayName\":\"林晚\"}");
    await Check(arranger, HttpMethod.Post, "/api/auth/users", 200, "{\"username\":\"suli\",\"password\":\"longenough\",\"role\":\"Viewer\"}");
    await Check(arranger, HttpMethod.Post, "/api/auth/users", 200, "{\"username\":\"chenmo\",\"password\":\"longenough\",\"displayName\":\"陈默\",\"role\":\"Editor\"}");
    using var viewerOnly = CookieClient();
    await Check(viewerOnly, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"suli\",\"password\":\"longenough\"}");
    using var rival = CookieClient();
    await Check(rival, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"chenmo\",\"password\":\"longenough\"}");

    string PlanBody(string? scope = null, bool overrideManual = false) => JsonSerializer.Serialize(new { scope, overrideManual });
    string ApplyBody(long revision, string? scope = null) =>
        JsonSerializer.Serialize(new { baseRevision = revision, scope, overrideManual = false, client = "web" });

    var layoutBytesBefore = File.ReadAllBytes(layoutCanvas);
    var revisionBefore = (await Check(arranger, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64();

    // 只读账号连预览都不给：它算的是「你会改什么」，给了只会让人以为能点应用。
    Assert((await Check(viewerOnly, HttpMethod.Post, "/api/web/layout/plan", 403, PlanBody()))
        .GetProperty("code").GetString() == "CANVAS_EDIT_FORBIDDEN", "只读账号不该能预览整理");

    var plan = await Check(arranger, HttpMethod.Post, "/api/web/layout/plan", 200, PlanBody());
    Assert(plan.GetProperty("wholeCanvas").GetBoolean() && plan.GetProperty("changed").GetBoolean(), "整画布整理应当算出改动：" + plan.GetRawText());
    Assert(plan.GetProperty("lanes").GetArrayLength() > 0 && plan.GetProperty("changes").GetArrayLength() > 0, "泳道与改动清单都要回给界面");
    Assert(layoutBytesBefore.SequenceEqual(File.ReadAllBytes(layoutCanvas)), "预览**绝不能**写盘");

    // 章节范围：章节 ID 从泳道里拿，不去猜引擎的归属规则。
    var chapterId = plan.GetProperty("lanes").EnumerateArray()
        .First(lane => lane.GetProperty("kind").GetString() == "Chapter").GetProperty("chapterId").GetString()!;
    Assert(!(await Check(arranger, HttpMethod.Post, "/api/web/layout/plan", 200, PlanBody(chapterId))).GetProperty("wholeCanvas").GetBoolean(),
        "章节范围不该说自己在整画布排");
    await Check(arranger, HttpMethod.Post, "/api/web/layout/plan", 400, PlanBody("not-a-guid"));

    // 陈旧修订：必须被拒，而且**字节不变**。
    await Check(arranger, HttpMethod.Post, "/api/web/layout/apply", 409, ApplyBody(revisionBefore + 1));
    Assert(layoutBytesBefore.SequenceEqual(File.ReadAllBytes(layoutCanvas)), "陈旧修订的应用不该改动画布");
    // 干跑挡住了它，所以也**不该因此占住整棵树锁**——否则一次手滑会把别人的结构改动挡到锁过期。
    Assert((await Check(arranger, HttpMethod.Get, "/api/web/edits", 200)).GetProperty("leases").GetArrayLength() == 0,
        "注定失败的整理不该占住整棵树锁");

    // 别人正拿着整棵树锁时，整理请求要被挡住并说清等谁（此时画布确实还没排好，所以这条是真的挡住了改动）。
    var rivalLease = (await Check(rival, HttpMethod.Post, "/api/web/edits", 200,
        "{\"scope\":\"tree\",\"targetId\":null,\"client\":\"desktop\"}")).GetProperty("lease").GetProperty("leaseId").GetGuid();
    var conflicted = await Check(arranger, HttpMethod.Post, "/api/web/layout/apply", 409, ApplyBody(revisionBefore));
    Assert(conflicted.GetProperty("code").GetString() == "EDIT_CONFLICT", "有人拿着整棵树锁时不该硬闯");
    Assert(conflicted.GetProperty("holder").GetProperty("displayName").GetString() == "陈默", "冲突要说清等谁：" + conflicted.GetRawText());
    Assert(layoutBytesBefore.SequenceEqual(File.ReadAllBytes(layoutCanvas)), "被锁挡住时不该改动画布");
    await Check(rival, HttpMethod.Delete, $"/api/web/edits/{rivalLease}", 200);

    // ---------- 变更推送（SSE）：谁改了什么，别人得立刻知道，而不是靠轮询撞见 ----------
    // 这条流会说「谁在编辑、谁改了什么」，所以匿名读不到——不过拦住它的是更外层的访问守卫（401），
    // 连处理函数都进不去。处理函数里那道 403 挡的是另一种情况：「有身份，但一个权限都没有」。
    using var anonymousEvents = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    await Check(anonymousEvents, HttpMethod.Get, "/api/web/events", 401);

    // 桌面桥的令牌能过守卫但 claims 是空的——正好走到处理函数里那道闸门。
    // 顺带用上了「改配置对运行中的服务立刻生效」这条（别处的实时收回权限用例也靠它）。
    using (var claimless = new HttpClient { BaseAddress = anonymousEvents.BaseAddress })
    {
        claimless.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-value");
        SetClaims("");
        try
        {
            var refused = false;
            for (var attempt = 0; attempt < 30 && !refused; attempt++)
            {
                using var probe = new HttpRequestMessage(HttpMethod.Get, "/api/web/events");
                // 只读响应头：配置热更新还没落地时这一下会是 200 的长流，
                // 走默认的「读完整个响应体」会一直挂着直到 HttpClient 超时。
                using var response = await claimless.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead);
                if ((int)response.StatusCode != 403) { await Task.Delay(100); continue; }
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                refused = document.RootElement.GetProperty("code").GetString() == "EVENTS_REQUIRE_SESSION";
            }
            Assert(refused, "没有权限的调用方不该能订阅变更推送");
        }
        finally { SetClaims("canvas.edit,skill.invoke,job.cancel"); }
    }

    // 订阅放在整理之前，下面那次真的写入才会被看到。
    using var eventResponse = await rival.GetAsync("/api/web/events", HttpCompletionOption.ResponseHeadersRead);
    Assert((int)eventResponse.StatusCode == 200, "登录后订阅变更推送应当 200：" + (int)eventResponse.StatusCode);
    Assert(eventResponse.Content.Headers.ContentType?.MediaType == "text/event-stream", "推送必须是 text/event-stream");
    var eventReader = new StreamReader(await eventResponse.Content.ReadAsStreamAsync());
    var pendingFrame = ReadFrame(eventReader);
    async Task<(string Type, JsonElement Data)?> NextEvent(int milliseconds)
    {
        // 超时**不取消**那条读——它继续挂着，下一帧真来了照样接得住。
        // 取消会把底层连接弄坏，那之后所有「没有收到」的断言都成了假阴性。
        if (await Task.WhenAny(pendingFrame, Task.Delay(milliseconds)) != pendingFrame) return null;
        var frame = await pendingFrame;
        pendingFrame = ReadFrame(eventReader);
        return frame;
    }
    // 除了开头那行 retry（重连间隔）之外，没动静时不该收到任何东西。
    Assert(await NextEvent(1500) is null, "订阅之后不该凭空收到事件");

    // 真正应用：落盘坐标必须与预览逐项一致——计划是服务端按同一份引擎重算的，所以两者等价。
    var applied = await Check(arranger, HttpMethod.Post, "/api/web/layout/apply", 200, ApplyBody(revisionBefore));
    var moved = applied.GetProperty("moved").GetInt32();
    Assert(moved > 0 && applied.GetProperty("revision").GetInt64() != revisionBefore, "应用整理要真的写入并推进修订");
    Assert(applied.GetProperty("records").GetArrayLength() == moved, "回给界面的记录数应当等于被移动的节点数");
    var planned = plan.GetProperty("changes").EnumerateArray().ToDictionary(
        change => Guid.Parse(change.GetProperty("recordId").GetString()!),
        change => (X: change.GetProperty("toX").GetSingle(), Y: change.GetProperty("toY").GetSingle()));
    var landed = 0;
    using (var document = JsonDocument.Parse(File.ReadAllText(layoutCanvas)))
    {
        foreach (var node in document.RootElement.GetProperty("Canvas").GetProperty("Nodes").EnumerateArray())
        {
            if (!planned.TryGetValue(node.GetProperty("Id").GetGuid(), out var expected)) continue;
            Assert(Math.Abs(node.GetProperty("X").GetSingle() - expected.X) < 0.001f &&
                Math.Abs(node.GetProperty("Y").GetSingle() - expected.Y) < 0.001f,
                "落盘坐标要与预览一致：" + node.GetProperty("Title").GetString());
            landed++;
        }
    }
    Assert(landed == planned.Count, $"预览里的每一项都应当真的落盘（{landed}/{planned.Count}）");
    Assert(Directory.GetFiles(Path.Combine(layoutCanvases, "backups"), "*.json").Length > 0, "整理布局也要留备份");

    // 整理写完之后订阅方应当收到一条 canvas.changed：scope=layout、不带单个记录号。
    // 重点在修订号——它必须是**接口回报给客户端的那个数**（项目模式下是内容哈希），
    // 客户端就是拿它和自己手上的比「谁更新」的；两边不是同一把尺子的话，这条推送等于没发。
    var layoutEvent = await NextEvent(5000);
    Assert(layoutEvent is not null && layoutEvent.Value.Type == "canvas.changed", "整理布局应当推一条 canvas.changed");
    var layoutPayload = layoutEvent!.Value.Data;
    Assert(layoutPayload.GetProperty("scope").GetString() == "layout" &&
        layoutPayload.GetProperty("recordId").ValueKind == JsonValueKind.Null,
        "整理的推送要说 scope=layout 且不带记录号：" + layoutPayload.GetRawText());
    Assert(layoutPayload.GetProperty("actor").GetString() == "林晚", "推送要说是谁整理的：" + layoutPayload.GetRawText());
    Assert(layoutPayload.GetProperty("revision").GetInt64() == applied.GetProperty("revision").GetInt64(),
        "推送里的修订号必须与接口回报的是同一个数：" + layoutPayload.GetRawText());
    // 整棵树锁只是为了让这次写入与别人排队，写完就该还回去；
    // 不还的话，一次整理会把别人的结构改动挡到锁过期，而界面上看不出是谁在挡。
    Assert((await Check(arranger, HttpMethod.Get, "/api/web/edits", 200)).GetProperty("leases").GetArrayLength() == 0,
        "整理布局用完的整棵树锁应当已经还回去");

    // 再应用一次：位置已经排好，就不该再写盘、也不该推进修订。这条同时证明了引擎与写入口是同一个口径。
    var revisionAfter = applied.GetProperty("revision").GetInt64();
    var bytesAfter = File.ReadAllBytes(layoutCanvas);
    var again = await Check(arranger, HttpMethod.Post, "/api/web/layout/apply", 200, ApplyBody(revisionAfter));
    Assert(again.GetProperty("moved").GetInt32() == 0 && again.GetProperty("revision").GetInt64() == revisionAfter,
        "已排好的画布再整理应当是「无需改动」：" + again.GetRawText());
    Assert(bytesAfter.SequenceEqual(File.ReadAllBytes(layoutCanvas)), "「无需改动」不该碰画布字节");

    // 改一个节点：推送要说清改的是哪个节点、谁改的，修订号也要与接口回报的一致。
    var editableId = applied.GetProperty("records")[0].GetProperty("recordId").GetString()!;
    var edited = await Check(rival, HttpMethod.Put, $"/api/web/records/{editableId}", 200,
        JsonSerializer.Serialize(new { baseRevision = revisionAfter, title = "改过的标题", content = "改过的内容" }));
    var recordEvent = await NextEvent(5000);
    Assert(recordEvent is not null && recordEvent.Value.Type == "canvas.changed", "改完节点应当收到 canvas.changed");
    var recordPayload = recordEvent!.Value.Data;
    Assert(recordPayload.GetProperty("scope").GetString() == "record" &&
        recordPayload.GetProperty("recordId").GetString() == editableId,
        "推送要说清改的是哪个节点：" + recordPayload.GetRawText());
    Assert(recordPayload.GetProperty("actor").GetString() == "陈默", "推送要说是谁改的：" + recordPayload.GetRawText());
    Assert(recordPayload.GetProperty("revision").GetInt64() == edited.GetProperty("revision").GetInt64(),
        "推送里的修订号必须与接口回报的是同一个数：" + recordPayload.GetRawText());

    // 被拒的写入（缺字段 → 400）不该广播：广播的判据是「真的落了盘」，不是「有人发过请求」。
    await Check(rival, HttpMethod.Put, $"/api/web/records/{editableId}", 400, "{}");
    Assert(await NextEvent(800) is null, "写入失败不该广播");

    // 「位置本来就排好」的整理也是 200，但画布一个字节都没变，同样不该广播。
    var noopApply = await Check(arranger, HttpMethod.Post, "/api/web/layout/apply", 200,
        ApplyBody(edited.GetProperty("revision").GetInt64()));
    Assert(noopApply.GetProperty("moved").GetInt32() == 0, "这一步的前提是整理确实无需改动");
    Assert(await NextEvent(800) is null, "「无需改动」不该广播");

    // 编辑锁：申请与释放要说，续期不必说（它只把到期时间往后推，界面上没有任何东西会变）。
    var pushedLease = (await Check(rival, HttpMethod.Post, "/api/web/edits", 200,
        "{\"scope\":\"node\",\"targetId\":\"" + editableId + "\",\"client\":\"web\"}"))
        .GetProperty("lease").GetProperty("leaseId").GetGuid();
    var acquiredEvent = await NextEvent(5000);
    Assert(acquiredEvent is not null && acquiredEvent.Value.Type == "edits.changed" &&
        acquiredEvent.Value.Data.GetProperty("reason").GetString() == "acquire" &&
        acquiredEvent.Value.Data.GetProperty("actor").GetString() == "陈默", "申请编辑锁应当推一条 edits.changed");
    Assert((await Check(rival, HttpMethod.Put, $"/api/web/edits/{pushedLease}", 200))
        .GetProperty("lease").GetProperty("leaseId").GetGuid() == pushedLease, "续期应当续的是同一个锁");
    Assert(await NextEvent(800) is null, "续期不该广播——界面上的东西一个都没变");
    await Check(rival, HttpMethod.Delete, $"/api/web/edits/{pushedLease}", 200);
    var releasedEvent = await NextEvent(5000);
    Assert(releasedEvent is not null && releasedEvent.Value.Type == "edits.changed" &&
        releasedEvent.Value.Data.GetProperty("reason").GetString() == "release", "释放编辑锁应当推一条 edits.changed");

    // 订阅是一条长连接，收工就断开，别把它拖进下一次重启。
    eventResponse.Dispose();

    // ---------- 桌面端接入：用桌面那份会话客户端登录同一台服务器 ----------
    // 这一段验的是「桌面端到底能不能接进来」：账号登录、看谁在编辑、会话失效要说清楚。
    // 对象就是 Desktop.Shared 里那份 CollaborationSession——桌面端界面将来用的就是它，
    // 所以这里通了，桌面端那一路就通了，剩下的只是把结果显示到界面上。
    //
    // 为什么不走桌面桥的 Bearer 令牌：编辑锁的意义是「显示谁在编辑」，那个令牌不带用户身份，
    // 服务端在锁接口上按设计拒绝它（EDIT_LEASE_REQUIRES_SESSION）。所以桌面端必须有账号。
    using (var desktop = new YEEYEEYEE.Desktop.CollaborationSession($"http://127.0.0.1:{port}"))
    {
        Assert(!desktop.IsSignedIn, "一开始不该是已登录状态");

        // 密码错：登不进去，而且**不能留下半个身份**。
        var denied = await desktop.SignInAsync("chenmo", "wrong-password");
        Assert(!denied.Ok && !string.IsNullOrWhiteSpace(denied.Message), "登录失败要带一句能给人看的话");
        Assert(!desktop.IsSignedIn && desktop.User is null, "登录失败了就不该有身份");

        var signedIn = await desktop.SignInAsync("chenmo", "longenough");
        Assert(signedIn.Ok && desktop.User?.DisplayName == "陈默", "登录要回身份：" + signedIn.Message);
        Assert(desktop.User?.RoleLabel == "编辑", "角色标签也要从服务端来");

        // 网页端的林晚占一个节点锁：桌面端应当看见它——这就是「谁在编辑」的来源。
        var webLease = (await Check(arranger, HttpMethod.Post, "/api/web/edits", 200,
            "{\"scope\":\"node\",\"targetId\":\"" + editableId + "\",\"client\":\"web\"}"))
            .GetProperty("lease").GetProperty("leaseId").GetGuid();
        var listed = await desktop.RefreshLeasesAsync();
        Assert(listed.Ok && desktop.Leases.Count == 1, "桌面端应当看见那一条编辑锁：" + listed.Message);
        Assert(desktop.Leases[0].DisplayName == "林晚" && desktop.Leases[0].Client == "web",
            "锁要带清持有者与来源端");
        Assert(desktop.Leases[0].TargetId == Guid.Parse(editableId), "节点锁要带目标节点");
        await Check(arranger, HttpMethod.Delete, $"/api/web/edits/{webLease}", 200);

        // 桌面端自己占住同一条锁：网页端的**别人**（林晚）应当被挡住，
        // 而且要从冲突里看出「这是桌面端占的、持有者是陈默」。
        // 用林晚而不是陈默去抢，是因为服务端允许同一个人跨端续期（自己不该挡自己）——
        // 拿同一个人去试「挡住没有」，测的其实是「自己抢自己」，等于什么都没验。
        var desktopNodeId = Guid.Parse(editableId);
        var desktopLease = await desktop.AcquireNodeLeaseAsync(desktopNodeId);
        Assert(desktopLease.Ok && desktop.HeldNodeLease?.TargetId == desktopNodeId, "桌面端要能占住节点锁：" + desktopLease.Message);
        Assert(desktop.HeldNodeLease?.Client == "desktop", "来源端要声明成桌面端，别人看到的就是「某某（桌面端）」");

        var blockedByDesktop = await Check(arranger, HttpMethod.Post, "/api/web/edits", 409,
            "{\"scope\":\"node\",\"targetId\":\"" + editableId + "\",\"client\":\"web\"}");
        Assert(blockedByDesktop.GetProperty("code").GetString() == "EDIT_CONFLICT", "桌面端占的锁要真的挡住网页端");
        Assert(blockedByDesktop.GetProperty("holder").GetProperty("client").GetString() == "desktop", "冲突要说清是桌面端占的");
        Assert(blockedByDesktop.GetProperty("holder").GetProperty("displayName").GetString() == "陈默", "冲突要说是谁占的");
        Assert(blockedByDesktop.GetProperty("message").GetString()!.Contains("陈默"), "冲突文案要带持有者名字");

        // 续期续的还是同一条锁；还回去之后别人就能占了。
        var heldId = desktop.HeldNodeLease!.LeaseId;
        Assert((await desktop.RenewHeldLeaseAsync()).Ok && desktop.HeldNodeLease?.LeaseId == heldId, "续期要保住同一条锁");
        Assert((await desktop.ReleaseHeldLeaseAsync()).Ok && desktop.HeldNodeLease is null, "还回去要把本地记录也清掉");

        var webRetry = (await Check(arranger, HttpMethod.Post, "/api/web/edits", 200,
            "{\"scope\":\"node\",\"targetId\":\"" + editableId + "\",\"client\":\"web\"}"))
            .GetProperty("lease").GetProperty("leaseId").GetGuid();
        await Check(arranger, HttpMethod.Delete, $"/api/web/edits/{webRetry}", 200);

        // 退出之后本地身份与 cookie 都要清掉：不能拿着旧身份接着用。
        Assert((await desktop.SignOutAsync()).Ok && !desktop.IsSignedIn, "退出要清掉身份");
        Assert(!(await desktop.RefreshLeasesAsync()).Ok, "没登录就不该能看谁在编辑");
    }

    // ---------- 桌面端把保存交给服务端：整画布写入 ----------
    // 桌面端自己写的是本地文件：既不经锁仲裁，别人也收不到通知。这条路把它交给服务端，
    // 于是锁真正管得住它、改动也顺便推给所有订阅者。跑的还是桌面那份客户端（CollaborationSession），
    // 所以这里通了，桌面端「保存修订」那一路就通了。
    using (var writer = new YEEYEEYEE.Desktop.CollaborationSession($"http://127.0.0.1:{port}"))
    {
        using var writeEvents = await rival.GetAsync("/api/web/events", HttpCompletionOption.ResponseHeadersRead);
        var writeReader = new StreamReader(await writeEvents.Content.ReadAsStreamAsync());
        var writePending = ReadFrame(writeReader);
        async Task<(string Type, JsonElement Data)?> NextWriteEvent(int milliseconds)
        {
            if (await Task.WhenAny(writePending, Task.Delay(milliseconds)) != writePending) return null;
            var frame = await writePending;
            writePending = ReadFrame(writeReader);
            return frame;
        }

        Assert((await writer.SignInAsync("chenmo", "longenough")).Ok, "桌面端先登录");
        var localRevision = YEEYEEYEE.Desktop.CanvasRevision.Of(File.ReadAllBytes(layoutCanvas));
        var handshake = await writer.CanvasRevisionAsync();
        Assert(handshake.Revision == localRevision,
            "握手：服务端那张画布的修订必须等于本地文件的哈希——这也正是「两边看的是同一张画布」的判据");

        // 改一个节点的标题，把**整张画布**交给服务端写（请求体就是画布字节）。
        var cwEdited = JsonNode.Parse(File.ReadAllText(layoutCanvas))!;
        cwEdited["Canvas"]!.AsObject()["Nodes"]!.AsArray()[0]!["Title"] = "桌面端改的标题";
        var payload = Encoding.UTF8.GetBytes(cwEdited.ToJsonString());
        var cwSaved = await writer.SaveCanvasAsync(localRevision, payload);
        Assert(cwSaved.Ok, "整画布写入应当成功：" + cwSaved.Message);
        Assert(YEEYEEYEE.Desktop.CanvasRevision.Of(File.ReadAllBytes(layoutCanvas)) == cwSaved.Revision,
            "服务端回的修订号必须等于它刚写下的那份字节（客户端就是拿它核对「写的是不是同一张」的）");
        // 落盘的写法会把非 ASCII 转义（画布里存的就是 \uXXXX），所以这里解析回来比对，
        // 而不是去原文里找那串中文。
        var landedTitle = JsonNode.Parse(File.ReadAllText(layoutCanvas))!
            ["Canvas"]!.AsObject()["Nodes"]!.AsArray()[0]!["Title"]!.GetValue<string>();
        Assert(landedTitle == "桌面端改的标题", "改动要真的落盘，实际是：" + landedTitle);

        // 别人应当收到一条 canvas.changed：这就是「桌面改一下、网页跟着变」的那条链，
        // 也正是「保存交给服务端」相比「自己写本地文件」多出来的东西。
        var pushed = await NextWriteEvent(5000);
        Assert(pushed is not null && pushed.Value.Type == "canvas.changed", "整画布写入也要广播");
        Assert(pushed!.Value.Data.GetProperty("scope").GetString() == "canvas",
            "广播要说清这是整画布级的改动：" + pushed.Value.Data.GetRawText());
        Assert(pushed.Value.Data.GetProperty("revision").GetInt64() == cwSaved.Revision, "广播里的修订号要与写入回报的一致");

        // 陈旧修订：必须被拒，而且字节不变（别人那份改动不能被盖掉）。
        var bytesBefore = File.ReadAllBytes(layoutCanvas);
        var stale = await writer.SaveCanvasAsync(localRevision, payload);
        Assert(!stale.Ok && stale.Code == "SCENE_REVISION_CONFLICT", "用旧修订保存必须被拒：" + stale.Message);
        Assert(bytesBefore.SequenceEqual(File.ReadAllBytes(layoutCanvas)), "被拒的写入不该动文件");

        // 别人正占着一个节点：整画布写入按**结构级**对待，要被挡住并说清等谁。
        var cwHold = (await Check(arranger, HttpMethod.Post, "/api/web/edits", 200,
            "{\"scope\":\"node\",\"targetId\":\"" + editableId + "\",\"client\":\"web\"}"))
            .GetProperty("lease").GetProperty("leaseId").GetGuid();
        var cwBlocked = await writer.SaveCanvasAsync(cwSaved.Revision, payload);
        Assert(!cwBlocked.Ok && cwBlocked.Code == "EDIT_CONFLICT", "有人占着节点时不该硬写整画布：" + cwBlocked.Message);
        Assert(cwBlocked.Holder?.DisplayName == "林晚", "冲突要说清等谁");
        Assert(bytesBefore.SequenceEqual(File.ReadAllBytes(layoutCanvas)), "被锁挡住时不该动文件");
        await Check(arranger, HttpMethod.Delete, $"/api/web/edits/{cwHold}", 200);

        // 送来的根本不是画布：如实拒绝，而不是写坏文件。
        var garbage = await writer.SaveCanvasAsync(cwSaved.Revision, Encoding.UTF8.GetBytes("这不是画布"));
        Assert(!garbage.Ok && garbage.Code == "CANVAS_INVALID", "送来的字节解析不了就要拒绝：" + garbage.Message);
        Assert(bytesBefore.SequenceEqual(File.ReadAllBytes(layoutCanvas)), "解析失败时不该动文件");
    }

    // ---------- 桌面端订阅变更推送 ----------
    // 桌面端把保存交给服务端之后，反过来也得收得到别人的改动，不然还要人自己去刷新。
    // 这一段验三件事：解析（认不出的安静丢掉）、真实推送能到、**退出登录之后不再推**。
    using (var watcher = new YEEYEEYEE.Desktop.CollaborationSession($"http://127.0.0.1:{port}"))
    {
        var notices = new List<YEEYEEYEE.Desktop.CanvasChangedNotice>();
        var editsPings = 0;
        Assert((await watcher.SignInAsync("chenmo", "longenough")).Ok, "订阅方先登录");
        watcher.StartWatching(notice => { lock (notices) notices.Add(notice); }, () => Interlocked.Increment(ref editsPings));

        // 网页端的林晚改一个节点：桌面端应当收到那条 canvas.changed。
        var subScene = await Check(arranger, HttpMethod.Get, "/api/web/scene", 200);
        var subEditing = subScene.GetProperty("records").EnumerateArray()
            .First(row => row.GetProperty("recordType").GetString() != "Chapter").GetProperty("recordId").GetString()!;
        await Check(arranger, HttpMethod.Put, $"/api/web/records/{subEditing}", 200,
            JsonSerializer.Serialize(new { baseRevision = subScene.GetProperty("revision").GetInt64(), title = "林晚改的", content = "内容" }));

        for (var attempt = 0; attempt < 40 && notices.Count == 0; attempt++) await Task.Delay(100);
        YEEYEEYEE.Desktop.CanvasChangedNotice? got;
        lock (notices) got = notices.FirstOrDefault();
        Assert(got is not null, "桌面端应当收到「画布有变动」");
        Assert(got!.Actor == "林晚" && got.RecordId == subEditing, "推送要说清是谁改了哪个节点：" + got);

        // 别人抢锁也会推一条 edits.changed：桌面端据此刷新锁列表。
        var subPing = (await Check(arranger, HttpMethod.Post, "/api/web/edits", 200,
            "{\"scope\":\"node\",\"targetId\":\"" + subEditing + "\",\"client\":\"web\"}"))
            .GetProperty("lease").GetProperty("leaseId").GetGuid();
        for (var attempt = 0; attempt < 40 && editsPings == 0; attempt++) await Task.Delay(100);
        Assert(editsPings > 0, "锁的进出也要推给桌面端");
        await Check(arranger, HttpMethod.Delete, $"/api/web/edits/{subPing}", 200);

        // 退出登录之后**不能再推**：服务端只在订阅那一刻校验过身份，
        // 客户端不主动断开的话，退出之后那条流还开着，界面会继续收到别人的改动。
        Assert((await watcher.SignOutAsync()).Ok, "退出登录");
        var beforeSignOut = notices.Count;
        await Check(arranger, HttpMethod.Put, $"/api/web/records/{subEditing}", 200,
            JsonSerializer.Serialize(new { baseRevision = (await Check(arranger, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64(), title = "退出之后改的", content = "内容" }));
        await Task.Delay(1200);
        Assert(notices.Count == beforeSignOut, "退出登录之后不该再收到推送");
    }

    // 独立场景模式下没有章节与泳道（引擎要的状态它没有），要如实说用不了，而不是拿裸 JSON 硬算。
    Stop();
    await Start(standalone: true, userDatabase: layoutDatabase);
    using var standaloneArranger = CookieClient();
    await Check(standaloneArranger, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"lin\",\"password\":\"longenough\"}");
    Assert((await Check(standaloneArranger, HttpMethod.Post, "/api/web/layout/plan", 409, PlanBody()))
        .GetProperty("code").GetString() == "LAYOUT_REQUIRES_PROJECT", "独立场景模式的整理应当明确拒绝");

    Stop();
    Console.WriteLine("HTTP regression passed: auth, live canvas.edit revocation, byte-preserving denials, jobs, assets, conflicts, persistence, and project/standalone modes");
    Console.WriteLine("Auth regression passed: first user becomes admin, role-derived claims, session persistence, disable/password revocation, setup token");
    Console.WriteLine("Edit-lease regression passed: node/tree granularity, idempotent acquire, holder identity, heartbeat renew, expiry vs missing, admin force takeover, corrupt-file self-healing, restart persistence, canvas bytes untouched");
    Console.WriteLine("Layout regression passed: shared swimlane engine on the server, chapter scope, stale revision, tree-lease arbitration, preview equals what lands on disk, idempotent no-op, standalone refusal");
    Console.WriteLine("Change-push regression passed: SSE subscribe gating, canvas.changed carrying recordId/actor/the same revision the API reports, silence on failure and on no-op layout, edits.changed on acquire/release but not on renew, tree lease returned after apply");
    Console.WriteLine("Desktop-client regression passed: account sign-in refuses a wrong password without leaving an identity behind, reads the shared lease list with holder and source client, sign-out clears the session");
    Console.WriteLine("Desktop canvas-write regression passed: handshake (server revision equals the local file hash), whole-canvas write lands and broadcasts canvas.changed with the same revision, stale base revision and someone else's node lease both refuse without touching the file, unparsable bytes rejected");
    // 帧的解析：真实帧里 data 与 type 是两个同名的字段（服务端往载荷里补了 type 与 at），
    // 两者必须一致才认——不一致说明有一边错了，宁可不认。认不出的类型安静丢掉。
    var parsedFrame = YEEYEEYEE.Desktop.CollaborationEvents.ParseCanvasChanged(
        "canvas.changed", "{\"type\":\"canvas.changed\",\"revision\":5,\"actor\":\"林晚\",\"scope\":\"record\"}");
    Assert(parsedFrame is { Actor: "林晚", Revision: 5, Scope: "record" }, "推送帧要能解析出来");
    Assert(YEEYEEYEE.Desktop.CollaborationEvents.ParseCanvasChanged("canvas.changed", "不是 json") is null &&
        YEEYEEYEE.Desktop.CollaborationEvents.ParseCanvasChanged("canvas.changed", "{\"type\":\"edits.changed\"}") is null &&
        YEEYEEYEE.Desktop.CollaborationEvents.ParseCanvasChanged("canvas.changed", "{\"revision\":5}") is null &&
        YEEYEEYEE.Desktop.CollaborationEvents.ParseCanvasChanged("canvas.unknown", "{\"type\":\"canvas.unknown\"}") is null,
        "坏 JSON、类型不符、缺 type、认不出的类型都要安静地丢掉（服务端以后加新事件时，旧客户端不该整条流都断掉）");
    Console.WriteLine("Desktop subscribe regression passed: frames parsed and unknown ones dropped, real canvas.changed and edits.changed reach the desktop, sign-out stops the stream");
}
finally { Stop(); try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
