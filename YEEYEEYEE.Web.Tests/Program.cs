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
async Task Start(string? token = "secret-value", string claims = "canvas.edit,skill.invoke,job.cancel", string approval = "preapproved-local-image", bool standalone = true, string? projectCanvas = null, string? userDatabase = null, string? setupToken = null, int? leaseSeconds = null, int? presenceSeconds = null)
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
    // 在线的兜底 TTL 也可配：用例要验「断开之后不再算在线」，就得把它缩到几秒，否则要干等两分钟。
    if (presenceSeconds is null) start.Environment.Remove("YEEYEEYEE__PresenceLifetimeSeconds");
    else start.Environment["YEEYEEYEE__PresenceLifetimeSeconds"] = presenceSeconds.Value.ToString();
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
// 首次建号要带初始化令牌：没配部署令牌时，那串是服务**首次启动时自己生成**的，
// 就写在账号库旁边（见 BootstrapToken）；真机上是人从 `docker compose logs` 里抄下来。
// 这个助手把它读出来带上——不然所有「先在空库上建个管理员」的用例都会撞在 403 上。
async Task<JsonElement> Setup(HttpClient client, string json, int status = 200)
{
    var tokenFile = Path.Combine(root, ".setup-token");
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/setup")
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    if (File.Exists(tokenFile)) request.Headers.Add("X-Setup-Token", File.ReadAllText(tokenFile).Trim());
    using var response = await client.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    if ((int)response.StatusCode != status) throw new Exception($"POST /api/auth/setup: {(int)response.StatusCode} expected {status}: {text}");
    return JsonDocument.Parse(text).RootElement.Clone();
}
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
/// <summary>在线名单里某个人那一项；不在名单里回 null。用来把「算一个人」与「basis/connections」逐项核实。</summary>
JsonElement? PresenceOf(JsonElement snapshot, Guid userId)
{
    foreach (var person in snapshot.GetProperty("people").EnumerateArray())
        if (person.GetProperty("userId").GetGuid() == userId) return person.Clone();
    return null;
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

    await Setup(admin, "{\"username\":\"lin\",\"password\":\"short\"}", 400);
    Assert((await Setup(admin, "{\"username\":\"林晚\",\"password\":\"longenough\"}", 400)).GetProperty("code").GetString() == "INVALID_USERNAME", "非法用户名要挡住");
    var created = await Setup(admin, "{\"username\":\"lin\",\"password\":\"longenough\",\"displayName\":\"林晚\"}");
    Assert(created.GetProperty("user").GetProperty("role").GetString() == "Admin", "第一个用户必须是管理员");
    var adminId = created.GetProperty("user").GetProperty("id").GetGuid();

    // 再建一次：必须 409，不能靠「先到先得」被第二个人抢成管理员。
    await Setup(admin, "{\"username\":\"other\",\"password\":\"longenough\"}", 409);

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

    // 但**空字符串的初始化令牌按「没配」算**：docker compose 里写 ${VAR:-} 时，
    // 变量没设传进来的就是一个空串。若当成「配了令牌」，界面会多出一个谁也填不出的输入框。
    // 「没配」现在是这个意思：**程序自己生成一把一次性钥匙**（见 BootstrapToken）——
    // 钥匙打在启动日志里、也写在账号库旁边，建出第一个管理员就作废。
    // 这样「谁先打开页面谁就是管理员」这条抢注路被堵上，而 docker compose up 仍然零准备可用。
    Stop();
    var blankTokenDatabase = Path.Combine(root, "users-blank-token.db");
    if (File.Exists(blankTokenDatabase)) File.Delete(blankTokenDatabase);
    // 文件名照 BootstrapToken.FileName 写（测试工程看不到那个 internal 类型）：
    // 放账号库旁边一个点开头的文件。
    var tokenFile = Path.Combine(root, ".setup-token");
    if (File.Exists(tokenFile)) File.Delete(tokenFile);
    await Start(userDatabase: blankTokenDatabase, setupToken: "");
    using var blank = CookieClient();
    Assert((await Check(blank, HttpMethod.Get, "/api/auth/state", 200)).GetProperty("setupTokenRequired").GetBoolean(),
        "没配部署令牌时要让界面要求填初始化令牌（那串由程序生成）");
    Assert(File.Exists(tokenFile), "一次性钥匙要写在账号库旁边，日志滚掉了还能从文件里捞");
    var bootstrap = File.ReadAllText(tokenFile).Trim();
    Assert(bootstrap.Length >= 16, "生成的令牌不能太短：" + bootstrap);
    Assert((await Check(blank, HttpMethod.Post, "/api/auth/setup", 403, "{\"username\":\"lin\",\"password\":\"longenough\"}"))
        .GetProperty("code").GetString() == "SETUP_TOKEN_REQUIRED", "不带令牌建号必须被拒（这条挡的就是抢注）");
    Assert((await Check(blank, HttpMethod.Post, "/api/auth/setup", 403,
        "{\"username\":\"lin\",\"password\":\"longenough\"}")).GetProperty("message").GetString()!.Contains("初始化令牌"),
        "拒绝理由要说清去哪儿找令牌");
    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/setup"))
    {
        request.Content = new StringContent("{\"username\":\"lin\",\"password\":\"longenough\"}", Encoding.UTF8, "application/json");
        request.Headers.Add("X-Setup-Token", bootstrap);
        using var response = await blank.SendAsync(request);
        Assert((int)response.StatusCode == 200, "带着那把一次性钥匙应当能建号：" + await response.Content.ReadAsStringAsync());
    }
    // 用过了就作废：文件删掉，界面也不再要求填它。
    Assert(!File.Exists(tokenFile), "建出管理员之后这把钥匙必须作废（文件要删掉）");
    using var afterSetup = CookieClient();
    Assert(!(await Check(afterSetup, HttpMethod.Get, "/api/auth/state", 200)).GetProperty("setupTokenRequired").GetBoolean(),
        "作废之后界面不该再要求填初始化令牌");
    Assert((await Check(afterSetup, HttpMethod.Post, "/api/auth/setup", 409, "{\"username\":\"lin2\",\"password\":\"longenough\"}"))
        .GetProperty("code").GetString() == "SETUP_ALREADY_DONE", "已经有管理员了就不该再建第二个");

    // 重启之后不该换钥匙：用户手上只有上一次日志里那串。
    Stop();
    var reusedDatabase = Path.Combine(root, "users-bootstrap-reuse.db");
    if (File.Exists(reusedDatabase)) File.Delete(reusedDatabase);
    if (File.Exists(tokenFile)) File.Delete(tokenFile);
    await Start(userDatabase: reusedDatabase, setupToken: "");
    var firstKey = File.ReadAllText(tokenFile).Trim();
    Stop();
    await Start(userDatabase: reusedDatabase, setupToken: "");
    Assert(File.ReadAllText(tokenFile).Trim() == firstKey, "重启不该换钥匙，否则刚抄下来的那串立刻作废");
    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/setup"))
    {
        request.Content = new StringContent("{\"username\":\"lin\",\"password\":\"longenough\"}", Encoding.UTF8, "application/json");
        request.Headers.Add("X-Setup-Token", firstKey);
        using var response = await CookieClient().SendAsync(request);
        Assert((int)response.StatusCode == 200, "重启后那把钥匙仍应能用：" + await response.Content.ReadAsStringAsync());
    }

    // 部署者自己配了令牌时，这套一次性钥匙完全不参与：用配的那把，而且**不生成文件**。
    Stop();
    var ownedDatabase = Path.Combine(root, "users-owned-token.db");
    if (File.Exists(ownedDatabase)) File.Delete(ownedDatabase);
    await Start(userDatabase: ownedDatabase, setupToken: "deploy-secret");
    Assert(!File.Exists(tokenFile), "配了部署令牌时不该再生成一次性钥匙");
    using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/setup"))
    {
        request.Content = new StringContent("{\"username\":\"lin\",\"password\":\"longenough\"}", Encoding.UTF8, "application/json");
        request.Headers.Add("X-Setup-Token", "deploy-secret");
        using var response = await CookieClient().SendAsync(request);
        Assert((int)response.StatusCode == 200, "部署令牌仍应能用：" + await response.Content.ReadAsStringAsync());
    }
    // ---------- 编辑锁：谁在编辑、粒度、冲突、心跳、过期、强制接管 ----------
    // 锁是**会话状态**，不是文档内容：挨着画布放在 <画布>.edits.json，不写进画布文件。
    // 所以这里也断言它**不碰画布字节**。
    Stop();
    var leaseDatabase = Path.Combine(root, "users-lease.db");
    foreach (var leftover in new[] { leaseDatabase, leaseDatabase + "-wal", leaseDatabase + "-shm" })
        if (File.Exists(leftover)) File.Delete(leftover);
    var leasePath = scenePath + ".edits.json";
    if (File.Exists(leasePath)) File.Delete(leasePath);
    // 这段服务后面要跑编辑锁、推送、桌面端、结构写入与在线状态；在线的兜底 TTL 缩到 3 秒，
    // 好让「断开之后不再算在线」这个用例不用干等两分钟。
    await Start(userDatabase: leaseDatabase, presenceSeconds: 3);

    using var boss = CookieClient();
    await Setup(boss, "{\"username\":\"lin\",\"password\":\"longenough\",\"displayName\":\"林晚\"}");
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
    await Setup(arranger, "{\"username\":\"lin\",\"password\":\"longenough\",\"displayName\":\"林晚\"}");
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
        // 订阅是**后台**发起的（StartWatching 不阻塞），所以这里等它真的连上再写：
        // 不然写入可能早于订阅注册，那一条推送就丢了——测出来的会是「没收到」，而不是「没推」。
        await Task.Delay(300);

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

    // ---------- 结构级写入：网页端新建 / 删除节点 ----------
    // 这两件事改的是画布结构，所以按结构级对待：要账号、要 canvas.edit、要整棵树锁。
    {
        var stScene = await Check(arranger, HttpMethod.Get, "/api/web/scene", 200);
        var stBase = stScene.GetProperty("revision").GetInt64();
        var stNodes = stScene.GetProperty("records").EnumerateArray()
            .Count(row => row.GetProperty("recordType").GetString() != "chapter");
        var stChapter = stScene.GetProperty("records").EnumerateArray()
            .First(row => row.GetProperty("recordType").GetString() == "chapter")
            .GetProperty("record").GetProperty("chapterId").GetString()!;
        var stChapterWord = stScene.GetProperty("records").EnumerateArray()
            .First(row => row.GetProperty("recordType").GetString() == "chapter")
            .GetProperty("recordId").GetString()!;

        // 广播：结构改动也要推给别人，而且要说清这是结构级的。
        using var stEvents = await rival.GetAsync("/api/web/events", HttpCompletionOption.ResponseHeadersRead);
        var stReader = new StreamReader(await stEvents.Content.ReadAsStreamAsync());
        var stPending = ReadFrame(stReader);

        var stCreated = await Check(arranger, HttpMethod.Post, "/api/web/records", 200,
            "{\"baseRevision\":" + stBase + ",\"recordType\":\"character\",\"title\":\"新角色\",\"chapterId\":\"" + stChapter + "\"}");
        var stNodeId = stCreated.GetProperty("nodeId").GetString()!;
        var stRevision = stCreated.GetProperty("revision").GetInt64();
        Assert(Guid.TryParse(stNodeId, out _) && stRevision != stBase, "新建要回节点 ID 与推进后的修订");

        var stReread = await Check(arranger, HttpMethod.Get, "/api/web/scene", 200);
        var stAdded = stReread.GetProperty("records").EnumerateArray()
            .FirstOrDefault(row => row.GetProperty("recordId").GetString() == stNodeId);
        Assert(stAdded.ValueKind != JsonValueKind.Undefined, "新建的节点要出现在场景里");
        Assert(stAdded.GetProperty("recordType").GetString() == "character" &&
            stAdded.GetProperty("record").GetProperty("title").GetString() == "新角色",
            "类别与标题要照说的建");
        Assert(stAdded.GetProperty("record").GetProperty("chapterId").GetString() == stChapter, "要落在指定的章节里");
        Assert(stReread.GetProperty("records").EnumerateArray()
            .Count(row => row.GetProperty("recordType").GetString() != "chapter") == stNodes + 1, "节点数要多一个");

        var stPushed = await Task.WhenAny(stPending, Task.Delay(5000)) == stPending ? await stPending : null;
        Assert(stPushed is { Type: "canvas.changed" } stFrame && stFrame.Data.GetProperty("scope").GetString() == "structure",
            "结构改动要广播，并说清是结构级的：" + (stPushed is { } got ? got.Data.GetRawText() : "(没收到)"));

        // 认不出的类别、章节、不存在的锚点、陈旧修订：都如实拒绝，而不是猜着建。
        Assert((await Check(arranger, HttpMethod.Post, "/api/web/records", 400,
            "{\"baseRevision\":" + stRevision + ",\"recordType\":\"spaceship\"}")).GetProperty("code").GetString() == "CANVAS_UNKNOWN_RECORD_TYPE",
            "认不出的类别要拒绝（猜一个 general 会静默地建错）");
        Assert((await Check(arranger, HttpMethod.Post, "/api/web/records", 400,
            "{\"baseRevision\":" + stRevision + ",\"recordType\":\"chapter\"}")).GetProperty("code").GetString() == "CANVAS_CHAPTER_NOT_SUPPORTED",
            "章节还不能在网页端新建");
        Assert((await Check(arranger, HttpMethod.Post, "/api/web/records", 400,
            "{\"baseRevision\":" + stRevision + ",\"recordType\":\"prop\",\"chapterId\":\"" + Guid.NewGuid() + "\"}")).GetProperty("code").GetString() == "CANVAS_UNKNOWN_CHAPTER",
            "锚点不存在的章节要拒绝");
        await Check(arranger, HttpMethod.Post, "/api/web/records", 409,
            "{\"baseRevision\":" + stBase + ",\"recordType\":\"prop\"}");

        // 别人正占着节点时，结构改动要被挡住并说清等谁。
        using (var stHolder = new YEEYEEYEE.Desktop.CollaborationSession($"http://127.0.0.1:{port}"))
        {
            Assert((await stHolder.SignInAsync("chenmo", "longenough")).Ok, "锁的持有者先登录");
            Assert((await stHolder.AcquireNodeLeaseAsync(Guid.Parse(stNodeId))).Ok, "占住刚建的这个节点");
            var stBlocked = await Check(arranger, HttpMethod.Post, "/api/web/records", 409,
                "{\"baseRevision\":" + stRevision + ",\"recordType\":\"prop\"}");
            Assert(stBlocked.GetProperty("code").GetString() == "EDIT_CONFLICT", "结构改动要受树的锁约束");
            Assert(stBlocked.GetProperty("message").GetString()!.Contains("陈默"), "冲突要说清等谁");
            await stHolder.ReleaseHeldLeaseAsync();
        }

        // 没身份连试都不用试：更外层的身份闸门先拦下（401）。
        // 端点里那句 403「要账号」是留给**桌面桥**的（它有 canvas.edit 权限但没有用户身份），
        // 匿名浏览器请求根本走不到那儿。
        using (var stAnonymous = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") })
        {
            var stDenied = await Check(stAnonymous, HttpMethod.Post, "/api/web/records", 401,
                "{\"baseRevision\":" + stRevision + ",\"recordType\":\"prop\"}");
            Assert(stDenied.GetProperty("code").GetString() == "UNAUTHORIZED", "没身份不能建节点：" + stDenied.GetRawText());
        }

        // 章节是工作树条目、不是画布节点：从这条路删不到它（也就不可能误删）。
        await Check(arranger, HttpMethod.Delete, $"/api/web/records/{stChapterWord}?baseRevision={stRevision}", 400);

        // 删除：节点消失、节点数回到原样。
        await Check(arranger, HttpMethod.Delete, $"/api/web/records/{stNodeId}?baseRevision={stRevision}", 200);
        var stAfterDelete = await Check(arranger, HttpMethod.Get, "/api/web/scene", 200);
        Assert(stAfterDelete.GetProperty("records").EnumerateArray().All(row => row.GetProperty("recordId").GetString() != stNodeId),
            "删掉的节点不该还在场景里");
        Assert(stAfterDelete.GetProperty("records").EnumerateArray()
            .Count(row => row.GetProperty("recordType").GetString() != "chapter") == stNodes, "节点数要回到原样");

        // 连线也要投影出来（桌面端画布上一直画着它们，网页端此前完全看不到）。
        // 这段画布里本来一条连线都没有，所以直接往文件里写：**一条有效的 + 一条悬空的**，
        // 再看服务端投影了什么——「悬空的不投影」只有这样才能验到。
        var efJson = JsonNode.Parse(File.ReadAllText(layoutCanvas))!;
        var efCanvas = efJson["Canvas"]!.AsObject();
        var efNodes = efCanvas["Nodes"]!.AsArray();
        var efFirst = efNodes[0]!["Id"]!.GetValue<string>();
        var efSecond = efNodes[1]!["Id"]!.GetValue<string>();
        var efArray = efCanvas["Edges"] as JsonArray;
        if (efArray is null)
        {
            efArray = new JsonArray();
            efCanvas["Edges"] = efArray;
        }
        efArray.Add(new JsonObject { ["SourceNodeId"] = efFirst, ["TargetNodeId"] = efSecond });
        efArray.Add(new JsonObject { ["SourceNodeId"] = efFirst, ["TargetNodeId"] = Guid.NewGuid().ToString() });
        File.WriteAllBytes(layoutCanvas, Encoding.UTF8.GetBytes(efJson.ToJsonString()));

        var efScene = await Check(arranger, HttpMethod.Get, "/api/web/scene", 200);
        var efEdges = efScene.GetProperty("edges").EnumerateArray().ToList();
        Assert(efEdges.Count == 1, "有效的连线要投影、悬空的那条要跳过：" + efScene.GetProperty("edges").GetRawText());
        Assert(efEdges[0].GetProperty("sourceId").GetString() == efFirst &&
            efEdges[0].GetProperty("targetId").GetString() == efSecond, "两端要照原样投影");
        Assert(Guid.TryParse(efEdges[0].GetProperty("edgeId").GetString(), out _), "连线要有稳定的 ID（将来断开它要用）");

        // 那条悬空的连线只该活在上一步里：**画布一旦有校验错误就是只读的**（写入前会拦下），
        // 带着它往后走，后面每一次写入都会 409 CANVAS_VALIDATION_FAILED。
        // 这也正是读取端要容错、写入端要严格的原因：坏数据进得来（别人写的文件），但不能靠它继续写。
        efArray.RemoveAt(1);
        File.WriteAllBytes(layoutCanvas, Encoding.UTF8.GetBytes(efJson.ToJsonString()));
        var efClean = await Check(arranger, HttpMethod.Get, "/api/web/scene", 200);
        Assert(efClean.GetProperty("edges").EnumerateArray().Count() == 1, "摘掉悬空的那条之后，有效的那条仍要投影");

        // ---------- 结构级写入：网页端的「连接」与「断开」 ----------
        // 与新建 / 删除节点同一条规矩。验四件事：能建、拒绝的几种都如实拒绝、受树的锁约束、能断。
        var edBase = efClean.GetProperty("revision").GetInt64();

        // 广播：连线的进出也是结构级的。
        using (var edEvents = await rival.GetAsync("/api/web/events", HttpCompletionOption.ResponseHeadersRead))
        {
            var edReader = new StreamReader(await edEvents.Content.ReadAsStreamAsync());
            var edPending = ReadFrame(edReader);

            // efFirst → efSecond 上一步已经直接写进文件了（见上面那段），所以这里**反着连**一根。
            var edCreated = await Check(arranger, HttpMethod.Post, "/api/web/edges", 200,
                "{\"baseRevision\":" + edBase + ",\"sourceId\":\"" + efSecond + "\",\"targetId\":\"" + efFirst + "\"}");
            var edEdgeId = edCreated.GetProperty("edgeId").GetString()!;
            var edRevision = edCreated.GetProperty("revision").GetInt64();
            Assert(Guid.TryParse(edEdgeId, out _) && edRevision != edBase, "新建连线要回连线 ID 与推进后的修订");

            var edReread = await Check(arranger, HttpMethod.Get, "/api/web/scene", 200);
            var edList = edReread.GetProperty("edges").EnumerateArray().ToList();
            Assert(edList.Count == 2, "新建之后应当有两条连线：" + edReread.GetProperty("edges").GetRawText());
            Assert(edList.Any(item => item.GetProperty("edgeId").GetString() == edEdgeId &&
                item.GetProperty("sourceId").GetString() == efSecond && item.GetProperty("targetId").GetString() == efFirst),
                "新建的连线要按说的两端投影出来（方向不能反）");
            // 另一条是上一步直接写进文件的那条：手写的 JSON 里没有 Id 这个字段，所以它每次读都是新生成的 GUID。
            // 这次写入把它连同新连线一起落了盘，**从这一刻起它的 ID 才稳定**——断开时必须取这一份，
            // 取上一次读到的那个会 404（那条线在磁盘上已经不是那个 ID 了）。
            var edExisting = edList.First(item => item.GetProperty("edgeId").GetString() != edEdgeId)
                .GetProperty("edgeId").GetString()!;

            var edPushed = await Task.WhenAny(edPending, Task.Delay(5000)) == edPending ? await edPending : null;
            Assert(edPushed is { Type: "canvas.changed" } edFrame && edFrame.Data.GetProperty("scope").GetString() == "structure",
                "连线改动要广播，并说清是结构级的：" + (edPushed is { } gotEd ? gotEd.Data.GetRawText() : "(没收到)"));

            // 四种如实拒绝：自环、重复、端点不存在、陈旧修订。
            // 自环本可以留给画布校验去拦，但那时只能回一句「校验失败」，说不清是为什么。
            Assert((await Check(arranger, HttpMethod.Post, "/api/web/edges", 400,
                "{\"baseRevision\":" + edRevision + ",\"sourceId\":\"" + efFirst + "\",\"targetId\":\"" + efFirst + "\"}"))
                .GetProperty("code").GetString() == "CANVAS_EDGE_SELF_LOOP", "自环要拒绝");
            Assert((await Check(arranger, HttpMethod.Post, "/api/web/edges", 409,
                "{\"baseRevision\":" + edRevision + ",\"sourceId\":\"" + efSecond + "\",\"targetId\":\"" + efFirst + "\"}"))
                .GetProperty("code").GetString() == "CANVAS_EDGE_EXISTS", "重复的连线要拒绝（按节点对判，忽略端口）");
            Assert((await Check(arranger, HttpMethod.Post, "/api/web/edges", 404,
                "{\"baseRevision\":" + edRevision + ",\"sourceId\":\"" + Guid.NewGuid() + "\",\"targetId\":\"" + efFirst + "\"}"))
                .GetProperty("code").GetString() == "CANVAS_NODE_NOT_FOUND", "端点不存在要如实拒绝，而不是留一条悬空的线");
            Assert((await Check(arranger, HttpMethod.Post, "/api/web/edges", 404,
                "{\"baseRevision\":" + edRevision + ",\"sourceId\":\"" + efFirst + "\",\"targetId\":\"" + Guid.NewGuid() + "\"}"))
                .GetProperty("code").GetString() == "CANVAS_NODE_NOT_FOUND", "终点不存在也要说清是哪一头");
            await Check(arranger, HttpMethod.Post, "/api/web/edges", 409,
                "{\"baseRevision\":" + edBase + ",\"sourceId\":\"" + efFirst + "\",\"targetId\":\"" + Guid.NewGuid() + "\"}");
            await Check(arranger, HttpMethod.Post, "/api/web/edges", 400,
                "{\"baseRevision\":" + edRevision + ",\"sourceId\":\"不是 GUID\",\"targetId\":\"" + efFirst + "\"}");
            await Check(arranger, HttpMethod.Post, "/api/web/edges", 400, "{\"baseRevision\":" + edRevision + "}");

            // 别人正占着端点时，连线也要被挡住并说清等谁（与新建节点同一条约束）。
            using (var edHolder = new YEEYEEYEE.Desktop.CollaborationSession($"http://127.0.0.1:{port}"))
            {
                Assert((await edHolder.SignInAsync("chenmo", "longenough")).Ok, "锁的持有者先登录");
                Assert((await edHolder.AcquireNodeLeaseAsync(Guid.Parse(efFirst))).Ok, "占住其中一端");
                var edBlocked = await Check(arranger, HttpMethod.Post, "/api/web/edges", 409,
                    "{\"baseRevision\":" + edRevision + ",\"sourceId\":\"" + efSecond + "\",\"targetId\":\"" + efFirst + "\"}");
                Assert(edBlocked.GetProperty("code").GetString() == "EDIT_CONFLICT", "连线要受树的锁约束");
                Assert(edBlocked.GetProperty("message").GetString()!.Contains("陈默"), "冲突要说清等谁");
                await edHolder.ReleaseHeldLeaseAsync();
            }

            // 没身份连试都不用试：更外层的身份闸门先拦下（401）。
            using (var edAnonymous = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") })
            {
                var edDenied = await Check(edAnonymous, HttpMethod.Post, "/api/web/edges", 401,
                    "{\"baseRevision\":" + edRevision + ",\"sourceId\":\"" + efSecond + "\",\"targetId\":\"" + efFirst + "\"}");
                Assert(edDenied.GetProperty("code").GetString() == "UNAUTHORIZED", "没身份不能连线：" + edDenied.GetRawText());
            }

            // 断开：这里断的是**上一步直接写进文件**的那条，所以顺带验了
            // 「断开一条不是本次请求建出来的线」也照样成立。
            await Check(arranger, HttpMethod.Delete, $"/api/web/edges/{edExisting}?baseRevision={edRevision}", 200);
            var edAfter = await Check(arranger, HttpMethod.Get, "/api/web/scene", 200);
            var edLeft = edAfter.GetProperty("edges").EnumerateArray().ToList();
            Assert(edLeft.Count == 1 && edLeft[0].GetProperty("edgeId").GetString() == edEdgeId, "断开的连线要消失，另一条不动");
            var edAfterRevision = edAfter.GetProperty("revision").GetInt64();
            Assert((await Check(arranger, HttpMethod.Delete, $"/api/web/edges/{edExisting}?baseRevision={edAfterRevision}", 404))
                .GetProperty("code").GetString() == "CANVAS_EDGE_NOT_FOUND", "断开一条已经不存在的连线要如实说找不到");
            await Check(arranger, HttpMethod.Delete, $"/api/web/edges/not-a-guid?baseRevision={edAfterRevision}", 400);
        }

        // 删节点要**连带**删掉它的连线（与桌面端同一条语义）。这一条不拿现成数据验：
        // 现成的节点里，章节不能删、有下级的也不能删，所以临时建两个。
        var edPairA = (await Check(arranger, HttpMethod.Post, "/api/web/records", 200,
            "{\"baseRevision\":" + (await Check(arranger, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64() +
            ",\"recordType\":\"prop\",\"title\":\"连线甲\"}")).GetProperty("nodeId").GetString()!;
        var edPairB = (await Check(arranger, HttpMethod.Post, "/api/web/records", 200,
            "{\"baseRevision\":" + (await Check(arranger, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64() +
            ",\"recordType\":\"prop\",\"title\":\"连线乙\"}")).GetProperty("nodeId").GetString()!;
        var edPairScene = await Check(arranger, HttpMethod.Get, "/api/web/scene", 200);
        await Check(arranger, HttpMethod.Post, "/api/web/edges", 200,
            "{\"baseRevision\":" + edPairScene.GetProperty("revision").GetInt64() +
            ",\"sourceId\":\"" + edPairA + "\",\"targetId\":\"" + edPairB + "\"}");
        var edPairLinked = await Check(arranger, HttpMethod.Get, "/api/web/scene", 200);
        Assert(edPairLinked.GetProperty("edges").EnumerateArray().Count(item =>
            item.GetProperty("sourceId").GetString() == edPairA && item.GetProperty("targetId").GetString() == edPairB) == 1,
            "临时建的两个节点之间要真的连上");
        await Check(arranger, HttpMethod.Delete,
            $"/api/web/records/{edPairA}?baseRevision={edPairLinked.GetProperty("revision").GetInt64()}", 200);
        var edPairAfter = await Check(arranger, HttpMethod.Get, "/api/web/scene", 200);
        Assert(edPairAfter.GetProperty("edges").EnumerateArray().All(item =>
            item.GetProperty("sourceId").GetString() != edPairA && item.GetProperty("targetId").GetString() != edPairA),
            "删节点要连带删掉它的连线，否则画布校验会在写入时拦下（用户看到的是「删了却保存不了」）");

        // ---------- 节点位置：在画布上把它拖到别处 ----------
        // 这一条是**记录级**写入（不动画布结构），所以**不占整棵树锁**——
        // 下面「别人占着别的节点时照样能挪」那一条，就是钉住这个决定的。
        using (var mpEvents = await rival.GetAsync("/api/web/events", HttpCompletionOption.ResponseHeadersRead))
        {
            var mpReader = new StreamReader(await mpEvents.Content.ReadAsStreamAsync());
            var mpPending = ReadFrame(mpReader);

            var mpScene = await Check(arranger, HttpMethod.Get, "/api/web/scene", 200);
            var mpBase = mpScene.GetProperty("revision").GetInt64();
            // 拿「分镜一」当样本：它有坐标、不是章节、也没有别的节点挂在它下面。
            var mpTarget = efSecond;
            var mpStart = mpScene.GetProperty("records").EnumerateArray()
                .First(row => row.GetProperty("recordId").GetString() == mpTarget).GetProperty("record");
            Assert(mpStart.GetProperty("x").GetDouble() != 4321.5, "起点不能正好等于下面要写进去的坐标，否则断言等于没测");

            // 广播：位置改动按**记录级**报（不是结构级），并说清改的是哪一条。
            var mpMoved = await Check(arranger, HttpMethod.Put, $"/api/web/records/{mpTarget}/position", 200,
                "{\"baseRevision\":" + mpBase + ",\"x\":4321.5,\"y\":1234.25}");
            var mpRevision = mpMoved.GetProperty("revision").GetInt64();
            Assert(mpRevision != mpBase, "移动要推进修订");
            var mpRecord = mpMoved.GetProperty("record");
            Assert(mpRecord.GetProperty("recordId").GetString() == mpTarget, "回的是同一条记录");
            Assert(mpRecord.GetProperty("record").GetProperty("x").GetDouble() == 4321.5 &&
                mpRecord.GetProperty("record").GetProperty("y").GetDouble() == 1234.25, "落点要照说的写");

            var mpPushed = await Task.WhenAny(mpPending, Task.Delay(5000)) == mpPending ? await mpPending : null;
            Assert(mpPushed is { Type: "canvas.changed" } mpFrame &&
                mpFrame.Data.GetProperty("scope").GetString() == "record" &&
                mpFrame.Data.GetProperty("recordId").GetString() == mpTarget,
                "位置改动要按记录级广播，并说清改的是哪一条：" + (mpPushed is { } gotMp ? gotMp.Data.GetRawText() : "(没收到)"));

            var mpReread = (await Check(arranger, HttpMethod.Get, "/api/web/scene", 200))
                .GetProperty("records").EnumerateArray()
                .First(row => row.GetProperty("recordId").GetString() == mpTarget).GetProperty("record");
            Assert(mpReread.GetProperty("x").GetDouble() == 4321.5 && mpReread.GetProperty("y").GetDouble() == 1234.25,
                "重读要拿到新位置");

            // 落盘的不只是坐标，还有「手动摆放」这个标记——整理布局据此绕开它。
            // 投影里没有这个字段，所以直接看文件：这正是「文件是唯一权威」那句话该被验的地方。
            using (var mpDoc = JsonDocument.Parse(File.ReadAllText(layoutCanvas)))
            {
                var mpNode = mpDoc.RootElement.GetProperty("Canvas").GetProperty("Nodes").EnumerateArray()
                    .First(node => node.GetProperty("Id").GetString() == mpTarget);
                Assert(mpNode.GetProperty("X").GetDouble() == 4321.5 && mpNode.GetProperty("Y").GetDouble() == 1234.25,
                    "文件里的坐标要真的是这个落点");
                Assert(mpNode.GetProperty("ManualPosition").GetBoolean(), "拖过的节点要标上手动摆放");
            }

            // 拒绝的几种：陈旧修订、节点不存在、缺坐标、不是 GUID、坐标不是数字、坐标溢出成无穷大。
            await Check(arranger, HttpMethod.Put, $"/api/web/records/{mpTarget}/position", 409,
                "{\"baseRevision\":" + mpBase + ",\"x\":1,\"y\":1}");
            Assert((await Check(arranger, HttpMethod.Put, $"/api/web/records/{Guid.NewGuid()}/position", 404,
                "{\"baseRevision\":" + mpRevision + ",\"x\":1,\"y\":1}"))
                .GetProperty("code").GetString() == "RECORD_NOT_FOUND", "不存在的节点要如实拒绝");
            await Check(arranger, HttpMethod.Put, $"/api/web/records/{mpTarget}/position", 400,
                "{\"baseRevision\":" + mpRevision + ",\"x\":1}");
            await Check(arranger, HttpMethod.Put, "/api/web/records/not-a-guid/position", 400,
                "{\"baseRevision\":" + mpRevision + ",\"x\":1,\"y\":1}");
            Assert((await Check(arranger, HttpMethod.Put, $"/api/web/records/{mpTarget}/position", 400,
                "{\"baseRevision\":" + mpRevision + ",\"x\":\"12\",\"y\":1}"))
                .GetProperty("code").GetString() == "INVALID_REQUEST", "坐标不是数字要拒绝");
            // 1e40 是**有限**的 double（过得了接口那一层），但收成 float 就是 Infinity——
            // 所以「坐标必须是有限数」这道闸门必须留在真正落盘的那一层。
            Assert((await Check(arranger, HttpMethod.Put, $"/api/web/records/{mpTarget}/position", 400,
                "{\"baseRevision\":" + mpRevision + ",\"x\":1e40,\"y\":1}"))
                .GetProperty("code").GetString() == "CANVAS_POSITION_INVALID", "溢出成无穷大的坐标要在落盘前拦下");

            // 「位置没变」服务端**不单独判**（与「改标题内容」那条路同一个态度）：客户端会先挡一道
            // （拖出去又拖回来时根本不发请求），而这里再来一次也只是照写。
            // 把这件事钉成一条断言，是为了让「它不是漏的」这句话有个地方站着——
            // 只有整理布局会在服务端判「无需改动」，因为那一条要跑完整张画布的引擎。
            var mpNoopBase = (await Check(arranger, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64();
            var mpAgain = await Check(arranger, HttpMethod.Put, $"/api/web/records/{mpTarget}/position", 200,
                "{\"baseRevision\":" + mpNoopBase + ",\"x\":4321.5,\"y\":1234.25}");
            Assert(mpAgain.GetProperty("record").GetProperty("record").GetProperty("x").GetDouble() == 4321.5,
                "重复写同一个落点照旧成功（「无需改动」挡在客户端那一层，不在这里）");

            // **记录级**的要点：别人占着**别的**节点时，位置改动照样能做。
            // 要是按结构级对待，一个人挪一下卡片就会挡住别人的结构改动。
            using var mpHolder = new YEEYEEYEE.Desktop.CollaborationSession($"http://127.0.0.1:{port}");
            Assert((await mpHolder.SignInAsync("chenmo", "longenough")).Ok, "锁的持有者先登录");
            Assert((await mpHolder.AcquireNodeLeaseAsync(Guid.Parse(efFirst))).Ok, "占住另一个节点");
            var mpBusyBase = (await Check(arranger, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64();
            await Check(arranger, HttpMethod.Put, $"/api/web/records/{mpTarget}/position", 200,
                "{\"baseRevision\":" + mpBusyBase + ",\"x\":500.5,\"y\":600.5}");
            await mpHolder.ReleaseHeldLeaseAsync();
        }

        // 锁定的节点不能移动——这是**数据层**的强制，不该只在界面上挡。
        // 现成数据里没有锁定节点，所以直接往文件里加一个（校验器不看 IsLocked，加它不会让画布变成只读）。
        {
            var mpLockJson = JsonNode.Parse(File.ReadAllText(layoutCanvas))!;
            var mpLockNode = mpLockJson["Canvas"]!["Nodes"]!.AsArray().OfType<JsonObject>()
                .First(node => node["Id"]!.GetValue<string>() == efSecond);
            mpLockNode["IsLocked"] = true;
            File.WriteAllBytes(layoutCanvas, Encoding.UTF8.GetBytes(mpLockJson.ToJsonString()));
            var mpLockRevision = (await Check(arranger, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64();
            Assert((await Check(arranger, HttpMethod.Put, $"/api/web/records/{efSecond}/position", 409,
                "{\"baseRevision\":" + mpLockRevision + ",\"x\":7,\"y\":7}"))
                .GetProperty("code").GetString() == "NODE_LOCKED", "锁定的节点不能移动");
            mpLockNode["IsLocked"] = false;
            File.WriteAllBytes(layoutCanvas, Encoding.UTF8.GetBytes(mpLockJson.ToJsonString()));
        }

        // ---------- 节点类别：把节点改成另一种 ----------
        // 与「改标题内容」「移动位置」同一档：**记录级**（要 canvas.edit 与修订 CAS，不占树锁）。
        {
            var ctScene = await Check(arranger, HttpMethod.Get, "/api/web/scene", 200);
            var ctBase = ctScene.GetProperty("revision").GetInt64();
            var ctTarget = efSecond;
            var ctBefore = ctScene.GetProperty("records").EnumerateArray()
                .First(row => row.GetProperty("recordId").GetString() == ctTarget);
            Assert(ctBefore.GetProperty("recordType").GetString() != "prop", "改之前不能已经是 prop，否则这条断言等于没测");
            var ctY = ctBefore.GetProperty("record").GetProperty("y").GetDouble();

            using var ctEvents = await rival.GetAsync("/api/web/events", HttpCompletionOption.ResponseHeadersRead);
            var ctReader = new StreamReader(await ctEvents.Content.ReadAsStreamAsync());
            var ctPending = ReadFrame(ctReader);

            var ctChanged = await Check(arranger, HttpMethod.Put, $"/api/web/records/{ctTarget}/category", 200,
                "{\"baseRevision\":" + ctBase + ",\"recordType\":\"prop\"}");
            var ctRevision = ctChanged.GetProperty("revision").GetInt64();
            Assert(ctRevision != ctBase, "改类别要推进修订");
            Assert(ctChanged.GetProperty("record").GetProperty("recordType").GetString() == "prop", "响应要回改完之后的类别");

            var ctPushed = await Task.WhenAny(ctPending, Task.Delay(5000)) == ctPending ? await ctPending : null;
            Assert(ctPushed is { Type: "canvas.changed" } ctFrame &&
                ctFrame.Data.GetProperty("scope").GetString() == "record" &&
                ctFrame.Data.GetProperty("recordId").GetString() == ctTarget,
                "改类别要按记录级广播，并说清改的是哪一条：" + (ctPushed is { } gotCt ? gotCt.Data.GetRawText() : "(没收到)"));

            // 落盘的**只有类别**：坐标不该被这次改动碰到（那是移动那条路的事）。
            using (var ctDoc = JsonDocument.Parse(File.ReadAllText(layoutCanvas)))
            {
                var ctNode = ctDoc.RootElement.GetProperty("Canvas").GetProperty("Nodes").EnumerateArray()
                    .First(node => node.GetProperty("Id").GetString() == ctTarget);
                Assert(ctNode.GetProperty("Category").GetInt32() == (int)YEEYEEYEE.Desktop.NodeCategory.Prop,
                    "文件里的类别要跟着改");
                Assert(ctNode.GetProperty("Y").GetDouble() == ctY, "改类别不该动坐标");
            }

            // 拒绝的几种：认不出的类别、改成章节、改章节节点、不存在的节点、陈旧修订、缺字段、不是 GUID。
            Assert((await Check(arranger, HttpMethod.Put, $"/api/web/records/{ctTarget}/category", 400,
                "{\"baseRevision\":" + ctRevision + ",\"recordType\":\"spaceship\"}"))
                .GetProperty("code").GetString() == "CANVAS_UNKNOWN_RECORD_TYPE",
                "认不出的类别要拒绝（猜一个 general 会静默把一个角色改成通用）");
            Assert((await Check(arranger, HttpMethod.Put, $"/api/web/records/{ctTarget}/category", 400,
                "{\"baseRevision\":" + ctRevision + ",\"recordType\":\"chapter\"}"))
                .GetProperty("code").GetString() == "CANVAS_CHAPTER_NOT_SUPPORTED", "不能把节点改成章节");
            Assert((await Check(arranger, HttpMethod.Put, $"/api/web/records/{efFirst}/category", 400,
                "{\"baseRevision\":" + ctRevision + ",\"recordType\":\"general\"}"))
                .GetProperty("code").GetString() == "CANVAS_CHAPTER_NOT_SUPPORTED",
                "也不能改章节节点的类别——它那一整章会失去落点");
            await Check(arranger, HttpMethod.Put, $"/api/web/records/{Guid.NewGuid()}/category", 404,
                "{\"baseRevision\":" + ctRevision + ",\"recordType\":\"prop\"}");
            await Check(arranger, HttpMethod.Put, $"/api/web/records/{ctTarget}/category", 409,
                "{\"baseRevision\":" + ctBase + ",\"recordType\":\"general\"}");
            await Check(arranger, HttpMethod.Put, $"/api/web/records/{ctTarget}/category", 400,
                "{\"baseRevision\":" + ctRevision + "}");
            await Check(arranger, HttpMethod.Put, "/api/web/records/not-a-guid/category", 400,
                "{\"baseRevision\":" + ctRevision + ",\"recordType\":\"prop\"}");

            // **记录级**的要点：别人占着**别的**节点时照样能改（与移动那一条同理）。
            using (var ctHolder = new YEEYEEYEE.Desktop.CollaborationSession($"http://127.0.0.1:{port}"))
            {
                Assert((await ctHolder.SignInAsync("chenmo", "longenough")).Ok, "锁的持有者先登录");
                Assert((await ctHolder.AcquireNodeLeaseAsync(Guid.Parse(efFirst))).Ok, "占住另一个节点");
                var ctBusyBase = (await Check(arranger, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64();
                await Check(arranger, HttpMethod.Put, $"/api/web/records/{ctTarget}/category", 200,
                    "{\"baseRevision\":" + ctBusyBase + ",\"recordType\":\"general\"}");
                await ctHolder.ReleaseHeldLeaseAsync();
            }

            using var ctAnonymous = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var ctDeniedBase = (await Check(arranger, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64();
            await Check(ctAnonymous, HttpMethod.Put, $"/api/web/records/{ctTarget}/category", 401,
                "{\"baseRevision\":" + ctDeniedBase + ",\"recordType\":\"prop\"}");
        }

        // ---------- 备份不会无限增长（工程债 #9）----------
        // 项目画布的备份以前只涨不落：清理判的是「写的这份是不是当前项目」，那个条件在 Web 服务里永不成立。
        // 这里对同一份画布连写 12 次——不清理的话它旁边会多出 12 份备份。
        {
            var pruneBase = (await Check(arranger, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64();
            for (var index = 0; index < 12; index++)
            {
                var written = await Check(arranger, HttpMethod.Put, $"/api/web/records/{efSecond}", 200,
                    JsonSerializer.Serialize(new { baseRevision = pruneBase, title = "Prune " + index, content = "x" }));
                pruneBase = written.GetProperty("revision").GetInt64();
            }

            var keptBackups = Directory.GetFiles(Path.Combine(canvasDirectory, "backups"), "*.json").Length;
            Assert(keptBackups <= 10, "项目画布旁边的备份应被压在 10 份以内，实际 " + keptBackups);
        }
    }

    // 独立场景模式下没有章节与泳道（引擎要的状态它没有），要如实说用不了，而不是拿裸 JSON 硬算。
    // 在线兜底 TTL 一样缩到 3 秒：下一段的「断开之后不再算在线」要用到它。
    Stop();
    await Start(standalone: true, userDatabase: layoutDatabase, presenceSeconds: 3);
    using var standaloneArranger = CookieClient();
    await Check(standaloneArranger, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"lin\",\"password\":\"longenough\"}");
    Assert((await Check(standaloneArranger, HttpMethod.Post, "/api/web/layout/plan", 409, PlanBody()))
        .GetProperty("code").GetString() == "LAYOUT_REQUIRES_PROJECT", "独立场景模式的整理应当明确拒绝");
    // 连线也一样：独立场景是裸 JSON，没有节点集合可言，含糊地照着改只会写出一份它读不懂的文件。
    Assert((await Check(standaloneArranger, HttpMethod.Post, "/api/web/edges", 409,
        "{\"baseRevision\":0,\"sourceId\":\"" + Guid.NewGuid() + "\",\"targetId\":\"" + Guid.NewGuid() + "\"}"))
        .GetProperty("code").GetString() == "CANVAS_REQUIRES_PROJECT", "独立场景模式的新建连线应当明确拒绝");
    Assert((await Check(standaloneArranger, HttpMethod.Delete, $"/api/web/edges/{Guid.NewGuid()}?baseRevision=0", 409))
        .GetProperty("code").GetString() == "CANVAS_REQUIRES_PROJECT", "独立场景模式的断开连线应当明确拒绝");
    // 位置写入**两个模式都支持**（改位置与改标题是同一档，不该在一个模式里被拒）：
    // 独立场景的节点没有泳道，但坐标是它自己的字段。
    var standaloneScene = await Check(standaloneArranger, HttpMethod.Get, "/api/web/scene", 200);
    var standaloneTarget = standaloneScene.GetProperty("records")[0].GetProperty("recordId").GetString()!;
    await Check(standaloneArranger, HttpMethod.Put, $"/api/web/records/{standaloneTarget}/position", 200,
        "{\"baseRevision\":" + standaloneScene.GetProperty("revision").GetInt64() + ",\"x\":321,\"y\":654}");
    Assert((await Check(standaloneArranger, HttpMethod.Get, "/api/web/scene", 200))
        .GetProperty("records")[0].GetProperty("record").GetProperty("x").GetDouble() == 321,
        "独立场景模式下也要能写位置");
    // 类别也一样：独立场景存的就是协议形状，recordType 是它自己的字段。
    var standaloneAfterMove = (await Check(standaloneArranger, HttpMethod.Get, "/api/web/scene", 200)).GetProperty("revision").GetInt64();
    await Check(standaloneArranger, HttpMethod.Put, $"/api/web/records/{standaloneTarget}/category", 200,
        "{\"baseRevision\":" + standaloneAfterMove + ",\"recordType\":\"prop\"}");
    Assert((await Check(standaloneArranger, HttpMethod.Get, "/api/web/scene", 200))
        .GetProperty("records")[0].GetProperty("recordType").GetString() == "prop",
        "独立场景模式下也要能改类别");

    // ---------- 在线状态（谁在线）：看得见人，但**不接进锁或权限** ----------
    // 在线是「这个人此刻连着我们」，锁是「谁正在编辑哪个节点」——两条独立的依据，别用一个去推另一个。
    using (var presenceAnon = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") })
        await Check(presenceAnon, HttpMethod.Get, "/api/web/presence", 401);

    // 这一段的客户端都现建：前面经过多次重启，端口与账号库都换过，旧客户端指向的是死地址。
    using var presenceAdmin = CookieClient();
    var presenceAdminLogin = await Check(presenceAdmin, HttpMethod.Post, "/api/auth/login", 200, "{\"username\":\"lin\",\"password\":\"longenough\"}");
    var presenceAdminId = presenceAdminLogin.GetProperty("user").GetProperty("id").GetGuid();

    // 观察者（管理员林晚）先订阅 SSE：它用来在「别人上下线」时收到 presence.changed。
    using (var observerEvents = await presenceAdmin.GetAsync("/api/web/events", HttpCompletionOption.ResponseHeadersRead))
    {
        var observerReader = new StreamReader(await observerEvents.Content.ReadAsStreamAsync());
        var observerPending = ReadFrame(observerReader);
        async Task<(string Type, JsonElement Data)?> NextObserverEvent(int milliseconds)
        {
            if (await Task.WhenAny(observerPending, Task.Delay(milliseconds)) != observerPending) return null;
            var frame = await observerPending;
            observerPending = ReadFrame(observerReader);
            return frame;
        }
        Assert(await NextObserverEvent(1000) is null, "观察者订阅之后不该凭空收到事件");

        // 专用账号：独立于其它用例的订阅，好让「断开之后不再算在线」干净地成立。
        var presenceCreated = await Check(presenceAdmin, HttpMethod.Post, "/api/auth/users", 200,
            "{\"username\":\"presence1\",\"password\":\"longenough\",\"displayName\":\"在线甲\",\"role\":\"Editor\"}");
        var presenceId = presenceCreated.GetProperty("user").GetProperty("id").GetGuid();
        using var presenceUser = CookieClient();
        var presenceLogin = await Check(presenceUser, HttpMethod.Post, "/api/auth/login", 200,
            "{\"username\":\"presence1\",\"password\":\"longenough\"}");
        Assert(presenceLogin.GetProperty("user").GetProperty("id").GetGuid() == presenceId, "登录要回同一个人");

        // 登录本身就会刷新会话 last_seen_at：此刻他应当以「recent」（兜底依据）出现，而不是「connection」。
        var justLoggedIn = await Check(presenceAdmin, HttpMethod.Get, "/api/web/presence", 200);
        var recentEntry = PresenceOf(justLoggedIn, presenceId);
        Assert(recentEntry is not null && recentEntry.Value.GetProperty("basis").GetString() == "recent",
            "刚登录、还没订阅时应当以 recent 依据出现：" + justLoggedIn.GetRawText());

        // 订阅 SSE（开两条连接）：同一个人多端/多标签只算一个人，但要如实说他有几条连接。
        var connection1 = await presenceUser.GetAsync("/api/web/events", HttpCompletionOption.ResponseHeadersRead);
        var connection2 = await presenceUser.GetAsync("/api/web/events", HttpCompletionOption.ResponseHeadersRead);
        try
        {
            JsonElement? live = null;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                live = PresenceOf(await Check(presenceAdmin, HttpMethod.Get, "/api/web/presence", 200), presenceId);
                if (live is { } found && found.GetProperty("connections").GetInt32() == 2) break;
                await Task.Delay(100);
            }
            Assert(live is not null, "订阅之后名单里应当有他");
            var person = live!.Value;
            Assert(person.GetProperty("basis").GetString() == "connection", "有活连接时依据应当是 connection");
            Assert(person.GetProperty("connections").GetInt32() == 2, "两条连接要如实报成 2：" + person.GetRawText());
            Assert(person.GetProperty("clients").EnumerateArray().Select(client => client.GetString()).Contains("web"),
                "要说出他用的端：" + person.GetRawText());
            var occurrences = (await Check(presenceAdmin, HttpMethod.Get, "/api/web/presence", 200))
                .GetProperty("people").EnumerateArray().Count(item => item.GetProperty("userId").GetGuid() == presenceId);
            Assert(occurrences == 1, "同一个人开两个连接也只能算一个人，实际出现 " + occurrences + " 次");

            // 上线要广播：观察者应当收到一条 presence.changed(join)。
            var joined = await NextObserverEvent(5000);
            Assert(joined is not null && joined.Value.Type == "presence.changed" &&
                joined.Value.Data.GetProperty("reason").GetString() == "join" &&
                joined.Value.Data.GetProperty("actor").GetString() == "在线甲",
                "有人上线应当推一条 presence.changed(join)");
        }
        finally
        {
            connection1.Dispose();
            connection2.Dispose();
        }

        // 下线也要广播：两条连接都断开之后才算下线。
        var left = await NextObserverEvent(5000);
        Assert(left is not null && left.Value.Type == "presence.changed" &&
            left.Value.Data.GetProperty("reason").GetString() == "leave", "有人下线应当推一条 presence.changed(leave)");

        // 桌面端自己也要被算作在线：它订阅 SSE 时带 ?client=desktop，服务端据此记成「桌面端」。
        // 同一个人的多条连接要聚合到一个条目上（下面 lin 同时有网页端观察者与这台桌面端）。
        using (var desktopPresence = new YEEYEEYEE.Desktop.CollaborationSession($"http://127.0.0.1:{port}"))
        {
            Assert((await desktopPresence.SignInAsync("lin", "longenough")).Ok, "桌面端先登录");
            desktopPresence.StartWatching(_ => { }, () => { });
            JsonElement? desktopEntry = null;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                desktopEntry = PresenceOf(await Check(presenceAdmin, HttpMethod.Get, "/api/web/presence", 200), presenceAdminId);
                if (desktopEntry is { } found && found.GetProperty("clients").EnumerateArray()
                    .Any(client => client.GetString() == "desktop")) break;
                await Task.Delay(100);
            }
            Assert(desktopEntry is not null &&
                desktopEntry.Value.GetProperty("clients").EnumerateArray().Any(client => client.GetString() == "desktop"),
                "桌面端订阅之后应当以 desktop 端出现在在线名单：" + (desktopEntry?.GetRawText() ?? "不在名单里"));
        }

        // 断开之后不再以「活连接」算在线；会话的 last_seen 兜底也会随 TTL（这里 3 秒）过期而消失。
        await Task.Delay(4000);
        var afterLeave = await Check(presenceAdmin, HttpMethod.Get, "/api/web/presence", 200);
        Assert(PresenceOf(afterLeave, presenceId) is null,
            "断开并等过 TTL 之后，名单里不该还有他：" + afterLeave.GetRawText());
    }

    Stop();
    Console.WriteLine("HTTP regression passed: auth, live canvas.edit revocation, byte-preserving denials, jobs, assets, conflicts, persistence, and project/standalone modes");
    Console.WriteLine("Presence regression passed: SSE subscribe registers as connection basis with the live connection count, one person stays one regardless of connections, the two bases stay apart (recent before subscribing), join/leave broadcast presence.changed, disconnect plus TTL removes the person, anonymous gets 401");
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
    // 「别人改了画布之后要不要自动重载」是个纯判断，值得单独钉住：
    // 它的全部意义在于**别把用户手上的东西吞掉**，而这四行判断就是那条线。
    Assert(YEEYEEYEE.Desktop.RemoteCanvasChange.Decide(true, true, false, false) == YEEYEEYEE.Desktop.RemoteChangeAction.Ignore,
        "自己那次保存的回声要忽略");
    Assert(YEEYEEYEE.Desktop.RemoteCanvasChange.Decide(false, true, false, false) == YEEYEEYEE.Desktop.RemoteChangeAction.Reload,
        "本地干净就该自动跟上");
    Assert(YEEYEEYEE.Desktop.RemoteCanvasChange.Decide(false, true, true, false) == YEEYEEYEE.Desktop.RemoteChangeAction.NotifyOnly &&
        YEEYEEYEE.Desktop.RemoteCanvasChange.Decide(false, true, false, true) == YEEYEEYEE.Desktop.RemoteChangeAction.NotifyOnly &&
        YEEYEEYEE.Desktop.RemoteCanvasChange.Decide(false, false, false, false) == YEEYEEYEE.Desktop.RemoteChangeAction.NotifyOnly,
        "有未保存改动、正在编辑/保存、或者根本没开画布：都只说不动");
    // 「该不该自动推」同样是纯判断：开关默认关着，所以它平时永远该回 false——
    // 而 false 不是「失败」，是「这事不归我管」。这条区分决定了关掉开关之后会不会有噪声。
    Assert(!YEEYEEYEE.Desktop.AutoSync.ShouldPush(false, true, true, true, false),
        "开关关着就永远不推（默认就是关的）");
    Assert(!YEEYEEYEE.Desktop.AutoSync.ShouldPush(true, false, true, true, false) &&
        !YEEYEEYEE.Desktop.AutoSync.ShouldPush(true, true, false, true, false),
        "没配服务器、没登录都不推");
    Assert(!YEEYEEYEE.Desktop.AutoSync.ShouldPush(true, true, true, false, false) &&
        !YEEYEEYEE.Desktop.AutoSync.ShouldPush(true, true, true, true, true),
        "没有未落盘的改动不推；正在保存或退避中也不推");
    Assert(YEEYEEYEE.Desktop.AutoSync.ShouldPush(true, true, true, true, false),
        "开关打开、登录着、有改动、不忙：这才推");
    Console.WriteLine("Desktop subscribe regression passed: frames parsed and unknown ones dropped, real canvas.changed and edits.changed reach the desktop, sign-out stops the stream, auto-reload only when the local copy is clean and idle, auto-push only when the user turned it on");
    Console.WriteLine("Structure regression passed: create lands in the named chapter with the asked category and broadcasts scope=structure, unknown category/chapter/missing anchor/stale revision all refused, another user's node lease blocks it, viewer refused, chapter entries not deletable through this path, delete removes the node");
    Console.WriteLine("Edge regression passed: connect lands on the asked endpoints with a fresh GUID and broadcasts scope=structure, self-loop/duplicate/missing endpoint/malformed body/stale revision each refuse with their own code, another user's node lease arbitrates, anonymous refused, disconnect removes exactly that one, a node delete takes its edges along, standalone mode refuses both");
    Console.WriteLine("Node-position regression passed: a drag lands the exact coordinates and stamps ManualPosition in the file, broadcasts canvas.changed as record scope with that record id, stale revision/missing node/missing coordinate/non-numeric coordinate/float-overflowing coordinate each refuse with their own code, a repeated identical position is still accepted (the client is the one that skips it), another user's node lease elsewhere does not block it, a locked node refuses, and standalone mode supports it too");
    Console.WriteLine("Node-category regression passed: a change lands the asked recordType and leaves the coordinates alone (checked in the file), broadcasts canvas.changed as record scope with that record id, unknown recordType / to-chapter / from-chapter / missing node / missing field / malformed id / stale revision each refuse with their own code, another user's node lease elsewhere does not block it, anonymous refused, and standalone mode supports it too");
}
finally { Stop(); try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
