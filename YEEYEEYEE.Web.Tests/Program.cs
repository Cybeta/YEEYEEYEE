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
async Task Start(string? token = "secret-value", string claims = "canvas.edit,skill.invoke,job.cancel", string approval = "preapproved-local-image", bool standalone = true, string? projectCanvas = null)
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
    if (server is { HasExited: false }) { server.Kill(entireProcessTree: true); server.WaitForExit(); }
    server?.Dispose();
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
    Console.WriteLine("HTTP regression passed: auth, live canvas.edit revocation, byte-preserving denials, jobs, assets, conflicts, persistence, and project/standalone modes");
}
finally { Stop(); try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
