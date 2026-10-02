using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

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

    Stop();
    Console.WriteLine("HTTP regression passed: auth, live canvas.edit revocation, byte-preserving denials, jobs, assets, conflicts, persistence, and project/standalone modes");
    Console.WriteLine("Auth regression passed: first user becomes admin, role-derived claims, session persistence, disable/password revocation, setup token");
    Console.WriteLine("Edit-lease regression passed: node/tree granularity, idempotent acquire, holder identity, heartbeat renew, expiry vs missing, admin force takeover, corrupt-file self-healing, restart persistence, canvas bytes untouched");
}
finally { Stop(); try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
