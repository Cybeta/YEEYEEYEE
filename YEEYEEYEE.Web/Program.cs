using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Host;
using YEEYEEYEE.Web;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var databasePath = LegacyConfig.Text(configuration, "JobDatabasePath") ?? Path.Combine(AppContext.BaseDirectory, "yeeeyee.jobs.db");
var comfyUiBaseUrl = configuration["ComfyUI:BaseUrl"] ?? throw new InvalidOperationException("ComfyUI:BaseUrl 未配置");
var comfyUiClientId = configuration["ComfyUI:ClientId"];
var comfyUiCheckpoint = configuration["ComfyUI:Checkpoint"];
var assetDirectory = LegacyConfig.Text(configuration, "AssetDirectory") ?? Path.Combine(AppContext.BaseDirectory, "assets");
var pollingInterval = configuration.GetValue<TimeSpan?>("ComfyUI:PollingInterval") ?? TimeSpan.FromSeconds(2);
builder.Services.AddSingleton<IJobStore>(_ => new SqliteJobStore(databasePath));
builder.Services.AddHttpClient("comfyui", client =>
{
    client.BaseAddress = new Uri(comfyUiBaseUrl, UriKind.Absolute);
    client.Timeout = configuration.GetValue("ComfyUI:HttpTimeout", TimeSpan.FromSeconds(30));
});
builder.Services.AddSingleton<IInvocationExecutor>(services =>
{
    var http = services.GetRequiredService<IHttpClientFactory>().CreateClient("comfyui");
    var factory = new ComfyUiWorkflowFactory(comfyUiCheckpoint);
    return new ComfyUiExecutor(http, factory.Create, comfyUiClientId);
});
var comfyUiWebSocketBaseUrl = configuration["ComfyUI:WebSocketBaseUrl"] ?? comfyUiBaseUrl;
builder.Services.AddSingleton<ComfyUiProvider>(services => new ComfyUiProvider(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("comfyui"), assetDirectory,
    new Uri(comfyUiWebSocketBaseUrl, UriKind.Absolute), comfyUiClientId));
builder.Services.AddSingleton<IExternalTaskCanceller>(services => services.GetRequiredService<ComfyUiProvider>());
builder.Services.AddSingleton<SingleMachineExecutionService>();
builder.Services.AddSingleton<ExternalTaskCallbackReceiver>(services =>
    new ExternalTaskCallbackReceiver(services.GetRequiredService<SingleMachineExecutionService>()));
builder.Services.AddSingleton<ExternalTaskPoller>(services => new ExternalTaskPoller(
    services.GetRequiredService<SingleMachineExecutionService>(), services.GetRequiredService<ComfyUiProvider>(), pollingInterval));
builder.Services.AddHostedService<ExternalTaskPollingHostedService>();
var callbackSecret = configuration["ComfyUI:CallbackSecret"];
if (!string.IsNullOrWhiteSpace(callbackSecret))
    builder.Services.AddSingleton(_ => new HmacCallbackVerifier(new CallbackSignatureOptions { Secret = Convert.FromBase64String(callbackSecret) }));

var app = builder.Build();
var execution = app.Services.GetRequiredService<SingleMachineExecutionService>();
var callbackReceiver = app.Services.GetRequiredService<ExternalTaskCallbackReceiver>();
var resourceReplaceRequests = new ConcurrentQueue<CanvasResourceReplaceRequest>();
var canvasDistPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "YEEYEEYEE.Canvas", "dist"));
var transport = new WebCanvasTransport();
var bridge = new HostBridge(transport, execution: execution);
bridge.Initialize(new SessionContext
{
    SessionId = Guid.NewGuid(), UserId = Guid.NewGuid(), ClientType = ClientType.Web,
    Role = MemberRole.Member, ServerClaims = new HashSet<string>(StringComparer.Ordinal) // legacy socket must never inherit HTTP execution privileges
});
bridge.ResourceReplaceRequested += request => resourceReplaceRequests.Enqueue(new CanvasResourceReplaceRequest(
    Guid.NewGuid(), request.RecordId, request.EntityId, request.VariantId, request.VariantVersionId));

WebSceneApi.Map(app);
WebSkillJobApi.Map(app);
app.UseWebSockets();
if (Directory.Exists(canvasDistPath))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(canvasDistPath) });
app.MapGet("/", () => Results.File(Path.Combine(canvasDistPath, "index.html"), "text/html"));
app.MapGet("/ws/canvas", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest) return Results.StatusCode(StatusCodes.Status400BadRequest);
    await transport.HandleWebSocketAsync(await context.WebSockets.AcceptWebSocketAsync(), context.RequestAborted);
    return Results.Empty;
});
// Legacy desktop bridge: retained for desktop compatibility but requires Bearer and loopback.
app.MapPost("/api/canvas/scene", async (HttpRequest request) =>
{
    var body = await JsonSerializer.DeserializeAsync<ScenePushRequest>(request.Body);
    if (body is null) return Results.BadRequest("请求体无效");
    bridge.SendScene(body.Records ?? [], body.Revision, body.Reason ?? "scene-update");
    return Results.Ok(new { status = "ok", revision = body.Revision });
});
app.MapPost("/api/canvas/nodes", async (HttpRequest request) =>
{
    var body = await JsonSerializer.DeserializeAsync<NodeUpdateRequest>(request.Body);
    if (body is null) return Results.BadRequest("请求体无效");
    bridge.SendNodeUpdates(body.Records ?? [], body.Revision);
    return Results.Ok(new { status = "ok", revision = body.Revision });
});
app.MapPost("/api/canvas/resource-replace", async (HttpRequest request) =>
{
    var body = await JsonSerializer.DeserializeAsync<CanvasResourceReplacePayload>(request.Body);
    if (body is null) return Results.BadRequest("请求体无效");
    var queued = new CanvasResourceReplaceRequest(Guid.NewGuid(), body.RecordId, body.EntityId, body.VariantId, body.VariantVersionId);
    resourceReplaceRequests.Enqueue(queued);
    return Results.Accepted(value: new { status = "queued", requestId = queued.RequestId });
});
app.MapGet("/api/canvas/resource-replace/next", () => resourceReplaceRequests.TryDequeue(out var request)
    ? Results.Ok(request) : Results.NoContent());
app.MapPost("/api/canvas/resource-replace/result", async (HttpRequest request) =>
{
    var body = await JsonSerializer.DeserializeAsync<CanvasResourceReplaceResult>(request.Body);
    if (body is null) return Results.BadRequest("请求体无效");
    bridge.SendResourceReplaceResult(body.RequestId, body.Ok, body.Message, body.Revision);
    return Results.Ok(new { status = "delivered" });
});
app.MapPost("/callbacks/comfyui/{userId:guid}", async (HttpRequest request, Guid userId) =>
{
    var verifier = request.HttpContext.RequestServices.GetService<HmacCallbackVerifier>();
    if (verifier is null) return Results.Problem("ComfyUI 回调密钥未配置", statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!request.Headers.TryGetValue("X-ComfyUI-Timestamp", out var timestamp)
        || !request.Headers.TryGetValue("X-ComfyUI-Nonce", out var nonce)
        || !request.Headers.TryGetValue("X-ComfyUI-Signature", out var signature))
        return Results.BadRequest("缺少回调签名头");
    await using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer, request.HttpContext.RequestAborted);
    try
    {
        var accepted = new SignedExternalCallbackHandler(verifier, callbackReceiver).Handle(
            userId, timestamp.ToString(), nonce.ToString(), signature.ToString(), buffer.ToArray());
        return accepted ? Results.Ok() : Results.NotFound("外部任务不存在");
    }
    catch (CallbackReplayException) { return Results.Unauthorized(); }
    catch (CryptographicException) { return Results.Unauthorized(); }
    catch (JsonException) { return Results.BadRequest("回调内容无效"); }
});
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

public sealed record CanvasResourceReplacePayload(
    [property: System.Text.Json.Serialization.JsonPropertyName("recordId")] Guid RecordId,
    [property: System.Text.Json.Serialization.JsonPropertyName("entityId")] Guid EntityId,
    [property: System.Text.Json.Serialization.JsonPropertyName("variantId")] Guid VariantId,
    [property: System.Text.Json.Serialization.JsonPropertyName("variantVersionId")] Guid? VariantVersionId);
public sealed record CanvasResourceReplaceRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("requestId")] Guid RequestId,
    [property: System.Text.Json.Serialization.JsonPropertyName("recordId")] Guid RecordId,
    [property: System.Text.Json.Serialization.JsonPropertyName("entityId")] Guid EntityId,
    [property: System.Text.Json.Serialization.JsonPropertyName("variantId")] Guid VariantId,
    [property: System.Text.Json.Serialization.JsonPropertyName("variantVersionId")] Guid? VariantVersionId);
public sealed record CanvasResourceReplaceResult(
    [property: System.Text.Json.Serialization.JsonPropertyName("requestId")] Guid RequestId,
    [property: System.Text.Json.Serialization.JsonPropertyName("ok")] bool Ok,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")] string Message,
    [property: System.Text.Json.Serialization.JsonPropertyName("revision")] int? Revision);
public sealed record ScenePushRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("records")] List<object>? Records,
    [property: System.Text.Json.Serialization.JsonPropertyName("revision")] int Revision,
    [property: System.Text.Json.Serialization.JsonPropertyName("reason")] string? Reason);
public sealed record NodeUpdateRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("records")] List<object>? Records,
    [property: System.Text.Json.Serialization.JsonPropertyName("revision")] int Revision);
