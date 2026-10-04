using System.Security.Cryptography;
using System.Text.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Host;
using YEEYEEYEE.Web;
using YEEYEEYEE.Web.Auth;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var databasePath = LegacyConfig.Text(configuration, "JobDatabasePath") ?? Path.Combine(AppContext.BaseDirectory, "yeeeyee.jobs.db");
var comfyUiBaseUrl = configuration["ComfyUI:BaseUrl"] ?? throw new InvalidOperationException("ComfyUI:BaseUrl 未配置");
var comfyUiClientId = configuration["ComfyUI:ClientId"];
var comfyUiCheckpoint = configuration["ComfyUI:Checkpoint"];
var assetDirectory = LegacyConfig.Text(configuration, "AssetDirectory") ?? Path.Combine(AppContext.BaseDirectory, "assets");
// 账号库与任务库分开存：任务库会随作业增长，账号库只有几十行；混在一起会让备份账号时顺带拖走一堆作业记录。
var userDatabasePath = LegacyConfig.Text(configuration, "UserDatabasePath") ?? Path.Combine(AppContext.BaseDirectory, "yeeeyee.users.db");
// 前端产物默认在源码树里，容器部署时它不在那个相对位置，所以允许用配置指定。
var configuredCanvasDist = LegacyConfig.Text(configuration, "CanvasDistPath");
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
// 账号库。建表在这里做一次（全是 IF NOT EXISTS），不要等到第一个请求进来才建——
// 那样第一个请求会同时踩到建表与写库，出问题时的报错会指向错误的地方。
var users = new UserStore(userDatabasePath);
users.EnsureSchema();
builder.Services.AddSingleton(users);
// 变更推送的中枢：进程内广播，不持久化。没人在听时发布是空操作。
builder.Services.AddSingleton<CanvasEventHub>();
var callbackSecret = configuration["ComfyUI:CallbackSecret"];
if (!string.IsNullOrWhiteSpace(callbackSecret))
    builder.Services.AddSingleton(_ => new HmacCallbackVerifier(new CallbackSignatureOptions { Secret = Convert.FromBase64String(callbackSecret) }));

var app = builder.Build();
var callbackReceiver = app.Services.GetRequiredService<ExternalTaskCallbackReceiver>();
var canvasDistPath = string.IsNullOrWhiteSpace(configuredCanvasDist)
    ? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "YEEYEEYEE.Canvas", "dist"))
    : Path.GetFullPath(configuredCanvasDist);

// 守卫必须在场景/技能接口之前注册：它把身份解析成 <see cref="WebAccessGuard.PermissionsItem"/>，
// 后面的写入判断靠它。
WebAccessGuard.Map(app, users);
AuthApi.Map(app, users, userDatabasePath);
WebSceneApi.Map(app);
// 编辑锁紧挨着场景接口注册：它按 WebCanvasMode 解出同一张画布，把锁文件放到画布旁边。
EditLeaseApi.Map(app);
// 整理布局用的是桌面端那份泳道引擎（Desktop.Shared），所以两端对同一张画布排出来的结果一致。
WebLayoutApi.Map(app);
// 变更推送（SSE）：场景、锁、布局三处写成功之后往这里发一条「变了」，客户端据此刷新。
WebEventApi.Map(app);

// 整画布写入（PUT /api/web/canvas）：桌面端把保存交给服务端，锁与修订都由服务端仲裁。
WebCanvasApi.Map(app);
WebSkillJobApi.Map(app);
WebSettingsApi.Map(app);
if (Directory.Exists(canvasDistPath))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(canvasDistPath) });
app.MapGet("/", () => Results.File(Path.Combine(canvasDistPath, "index.html"), "text/html"));
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
