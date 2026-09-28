using System.Security.Cryptography;
using System.Text.Json;
using DreamForge.Core;
using DreamForge.Host;
using DreamForge.Web;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

var databasePath = configuration["DreamForge:JobDatabasePath"]
    ?? Path.Combine(AppContext.BaseDirectory, "dreamforge.jobs.db");
var comfyUiBaseUrl = configuration["ComfyUI:BaseUrl"]
    ?? throw new InvalidOperationException("ComfyUI:BaseUrl 未配置");
var comfyUiClientId = configuration["ComfyUI:ClientId"];
var comfyUiCheckpoint = configuration["ComfyUI:Checkpoint"];
var assetDirectory = configuration["DreamForge:AssetDirectory"]
    ?? Path.Combine(AppContext.BaseDirectory, "assets");
var pollingInterval = configuration.GetValue<TimeSpan?>("ComfyUI:PollingInterval")
    ?? TimeSpan.FromSeconds(2);

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
    return new ComfyUiExecutor(
        http,
        factory.Create,
        comfyUiClientId);
});
var comfyUiWebSocketBaseUrl = configuration["ComfyUI:WebSocketBaseUrl"]
    ?? comfyUiBaseUrl;
builder.Services.AddSingleton<ComfyUiProvider>(services =>
    new ComfyUiProvider(
        services.GetRequiredService<IHttpClientFactory>().CreateClient("comfyui"),
        assetDirectory,
        new Uri(comfyUiWebSocketBaseUrl, UriKind.Absolute),
        comfyUiClientId));
builder.Services.AddSingleton<IExternalTaskCanceller>(services => services.GetRequiredService<ComfyUiProvider>());
builder.Services.AddSingleton<SingleMachineExecutionService>();
builder.Services.AddSingleton<ExternalTaskCallbackReceiver>(services =>
    new ExternalTaskCallbackReceiver(
        services.GetRequiredService<SingleMachineExecutionService>()));
builder.Services.AddSingleton<ExternalTaskPoller>(services =>
    new ExternalTaskPoller(
        services.GetRequiredService<SingleMachineExecutionService>(),
        services.GetRequiredService<ComfyUiProvider>(),
        pollingInterval));
builder.Services.AddHostedService<ExternalTaskPollingHostedService>();

var callbackSecret = configuration["ComfyUI:CallbackSecret"];
if (!string.IsNullOrWhiteSpace(callbackSecret))
{
    builder.Services.AddSingleton(_ => new HmacCallbackVerifier(
        new CallbackSignatureOptions
        {
            Secret = Convert.FromBase64String(callbackSecret)
        }));
}

var app = builder.Build();
var execution = app.Services.GetRequiredService<SingleMachineExecutionService>();
var callbackReceiver = app.Services.GetRequiredService<ExternalTaskCallbackReceiver>();

var transport = new WebCanvasTransport(Console.WriteLine);
var bridge = new HostBridge(transport, execution: execution);
bridge.Initialize(new SessionContext
{
    SessionId = Guid.NewGuid(),
    UserId = Guid.NewGuid(),
    ClientType = ClientType.Web,
    Role = MemberRole.Member,
    ServerClaims = new HashSet<string>(["canvas.edit", "skill.invoke", "job.cancel"])
});

app.MapPost("/callbacks/comfyui/{userId:guid}", async (HttpRequest request, Guid userId) =>
{
    var verifier = request.HttpContext.RequestServices
        .GetService<HmacCallbackVerifier>();
    if (verifier is null)
        return Results.Problem(
            "ComfyUI 回调密钥未配置",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    if (!request.Headers.TryGetValue("X-ComfyUI-Timestamp", out var timestamp)
        || !request.Headers.TryGetValue("X-ComfyUI-Nonce", out var nonce)
        || !request.Headers.TryGetValue("X-ComfyUI-Signature", out var signature))
        return Results.BadRequest("缺少回调签名头");

    await using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer, request.HttpContext.RequestAborted);
    var body = buffer.ToArray();

    try
    {
        var accepted = new SignedExternalCallbackHandler(verifier, callbackReceiver)
            .Handle(
                userId,
                timestamp.ToString(),
                nonce.ToString(),
                signature.ToString(),
                body);

        return accepted
            ? Results.Ok()
            : Results.NotFound("外部任务不存在");
    }
    catch (CallbackReplayException)
    {
        return Results.Unauthorized();
    }
    catch (CryptographicException)
    {
        return Results.Unauthorized();
    }
    catch (JsonException)
    {
        return Results.BadRequest("回调内容无效");
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();
