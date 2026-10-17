using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Web.Auth;

namespace YEEYEEYEE.Web;

internal sealed class ComfyUiConversion(IConfiguration configuration, IHostEnvironment environment) : IDisposable
{
    internal sealed record Input(string[] Paths, string? BaseUrl = null);
    internal sealed record Work(string RequestId, int WorkerId, string Session, string SourceHash, string Raw)
    {
        internal TaskCompletionSource<JsonObject> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal sealed class Session(Guid owner, Uri target, string origin)
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal string RequestId { get; } = Guid.NewGuid().ToString("N");
        internal Guid Owner { get; } = owner;
        internal Uri Target { get; } = target;
        internal string Origin { get; set; } = origin;
        internal CancellationTokenSource Stop { get; } = new(TimeSpan.FromMinutes(15));
        internal Channel<Work> Queue { get; } = Channel.CreateUnbounded<Work>();
        internal ConcurrentDictionary<string, Work> Pending { get; } = new();
        internal Task<ComfyUiLibraryResult> Result { get; set; } = null!;
        internal HttpClient Http { get; set; } = null!;
        private int cancelled;
        internal void Cancel()
        {
            if (Interlocked.Exchange(ref cancelled, 1) != 0) return;
            Stop.Cancel();
            Queue.Writer.TryComplete();
            foreach (var work in Pending.Values) work.Completion.TrySetCanceled();
            Pending.Clear();
            Http.Dispose();
        }
    }
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private Timer? cleanup;
    internal Uri? MainOrigin { get; private set; }
    internal Uri? ConversionOrigin { get; private set; }
    internal bool Configured => MainOrigin is not null && ConversionOrigin is not null;

    internal static Uri Target(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("ComfyUI 地址必须为无凭据、查询参数或片段的 HTTP/S 地址");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    internal static string BaseUrl(IConfiguration config, string? value = null) => Target(
        string.IsNullOrWhiteSpace(value) ? (string.IsNullOrWhiteSpace(WebSettingsApi.LoadModels().ComfyUiBaseUrl)
            ? config["ComfyUI:BaseUrl"]! : WebSettingsApi.LoadModels().ComfyUiBaseUrl) : value).AbsoluteUri.TrimEnd('/');

    internal HttpClient Client(Uri target)
    {
        ValidateTarget(target);
        var configuredTarget = Target(configuration["ComfyUI:BaseUrl"]!);
        var authorization = target == configuredTarget && !string.IsNullOrWhiteSpace(configuration["ComfyUI:Authorization"])
            ? AuthenticationHeaderValue.Parse(configuration["ComfyUI:Authorization"]!) : null;
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
        { BaseAddress = target, Timeout = TimeSpan.FromMinutes(4) };
        http.DefaultRequestHeaders.Authorization = authorization;
        return http;
    }
    private void ValidateTarget(Uri target)
    {
        if (new[] { MainOrigin, ConversionOrigin }.Any(origin => origin is not null &&
            (target.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
             target.IdnHost.EndsWith("." + origin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
             origin.IdnHost.EndsWith("." + target.IdnHost, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("ComfyUI 目标不能指向主站或转换站");
    }
    internal void Map(WebApplication app)
    {
        var main = configuration["ComfyUIConversion:MainOrigin"];
        var conversion = configuration["ComfyUIConversion:ConversionOrigin"];
        if (!string.IsNullOrWhiteSpace(main) || !string.IsNullOrWhiteSpace(conversion))
        {
            MainOrigin = Target(main ?? ""); ConversionOrigin = Target(conversion ?? "");
            if (MainOrigin.AbsolutePath != "/" || ConversionOrigin.AbsolutePath != "/" ||
                MainOrigin.IdnHost == ConversionOrigin.IdnHost ||
                MainOrigin.IdnHost.EndsWith("." + ConversionOrigin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                ConversionOrigin.IdnHost.EndsWith("." + MainOrigin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                (!environment.IsDevelopment() && (MainOrigin.Scheme != "https" || ConversionOrigin.Scheme != "https")))
                throw new InvalidOperationException("转换来源必须是不同主机的纯来源，生产环境必须使用 HTTPS");
        }
        cleanup = new Timer(_ =>
        {
            foreach (var pair in sessions.Where(pair => pair.Value.Stop.IsCancellationRequested).ToArray())
                if (sessions.TryRemove(pair.Key, out var expired)) expired.Cancel();
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            foreach (var pair in sessions.Where(pair => pair.Value.Stop.IsCancellationRequested).ToArray())
                if (sessions.TryRemove(pair.Key, out var expired)) expired.Cancel();
            var host = context.Request.Host.Host;
            if (ConversionOrigin is null) { await next(context); return; }
            if (MainOrigin is not null && host.Equals(MainOrigin.Host, StringComparison.OrdinalIgnoreCase) &&
                (context.Request.Scheme != MainOrigin.Scheme || context.Request.Host.Port.GetValueOrDefault(context.Request.IsHttps ? 443 : 80) != MainOrigin.Port))
            { context.Response.StatusCode = 404; return; }
            if (host.Equals(ConversionOrigin.Host, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + ConversionOrigin.Host, StringComparison.OrdinalIgnoreCase))
            {
                await Isolated(context); return;
            }
            if (MainOrigin is not null && (!host.Equals(MainOrigin.Host, StringComparison.OrdinalIgnoreCase) || context.Request.Host.Port.GetValueOrDefault(context.Request.IsHttps ? 443 : 80) != MainOrigin.Port))
            { context.Response.StatusCode = 404; return; }
            await next(context);
        });
        app.MapPost("/api/web/settings/workflow-conversions", (HttpContext context, Input input) =>
        {
            var user = WebAccessGuard.CurrentUser(context);
            if (user?.Role != UserRole.Admin) return Results.StatusCode(403);
            if (!Configured) return Results.Json(new { code = "CONVERSION_NOT_CONFIGURED", message = "请配置独立转换来源及通配 DNS/TLS" }, statusCode: 503);
            if (context.Request.Headers.Origin != MainOrigin!.GetLeftPart(UriPartial.Authority)) return Results.StatusCode(403);
            if (input.Paths is not { Length: > 0 and <= 500 } || input.Paths.Any(string.IsNullOrWhiteSpace)) return Results.BadRequest();
            try
            {
                var target = Target(BaseUrl(configuration, input.BaseUrl)); ValidateTarget(target);
                var session = new Session(user.Id, target, "");
                session.Origin = Origin(session.Id);
                session.Http = Client(target);
                if (!sessions.TryAdd(session.Id, session)) throw new InvalidOperationException();
                session.Stop.Token.Register(() => { foreach (var work in session.Pending.Values) work.Completion.TrySetCanceled(); });
                session.Result = Fetch(session, input.Paths);
                return Results.Json(new { id = session.Id, requestId = session.RequestId, url = session.Origin + "/__conversion/", origin = session.Origin });
            }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });
        app.MapPost("/api/web/settings/workflow-conversions/{id}/complete", async (HttpContext context, string id) =>
        {
            if (!Owned(context, id, out var session)) return Results.StatusCode(403);
            if (!session!.Result.IsCompleted) return Results.StatusCode(409);
            if (!sessions.TryRemove(id, out _)) return Results.StatusCode(409);
            try
            {
                var result = await session.Result;
                lock (WebSettingsApi.ModelGate)
                {
                    var previous = SiteCatalog.Load().Sites.FirstOrDefault(s => s.BaseUrl.TrimEnd('/') == result.BaseUrl.TrimEnd('/'));
                    var installed = ComfyUiLibrary.Install(result, "", configuration["ComfyUI:Checkpoint"] ?? "", previous);
                    if (installed.Site is null) return Results.Json(new { error = installed.Error }, statusCode: 503);
                }
                return Results.Json(new { result.Converted, result.Failed, workflows = result.Workflows.Select(w => new { w.Key, w.Title, w.Converted, w.Note, w.Error }) });
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
            { return Results.Json(new { error = ex is OperationCanceledException ? "工作流转换未完成或已过期" : ex.Message }, statusCode: 502); }
            finally { sessions.TryRemove(id, out _); session.Cancel(); }
        });
        app.MapDelete("/api/web/settings/workflow-conversions/{id}", (HttpContext context, string id) =>
        {
            if (!Owned(context, id, out var session)) return Results.StatusCode(403);
            sessions.TryRemove(id, out _); session!.Cancel(); return Results.NoContent();
        });
    }
    private static async Task<ComfyUiLibraryResult> Fetch(Session session, string[] paths)
    {
        try
        {
            return await ComfyUiLibrary.FetchAsync(session.Target.AbsoluteUri, session.Http, cancellationToken: session.Stop.Token,
                exporterFactory: (_, worker) => new Exporter(session, worker), selectedPaths: paths, requestedWorkerCount: 1);
        }
        finally { session.Http.Dispose(); session.Queue.Writer.TryComplete(); }
    }

    private bool Owned(HttpContext context, string id, out Session? session) => sessions.TryGetValue(id, out session) &&
        WebAccessGuard.CurrentUser(context) is { Role: UserRole.Admin } user && user.Id == session.Owner && !session.Stop.IsCancellationRequested;
    private string Origin(string label) => new UriBuilder(ConversionOrigin!) { Host = label + "." + ConversionOrigin!.Host }.Uri.GetLeftPart(UriPartial.Authority);
    private async Task Isolated(HttpContext context)
    {
        if (context.Request.Host.Host.Equals(ConversionOrigin!.Host, StringComparison.OrdinalIgnoreCase))
        { context.Response.StatusCode = 404; return; }
        var label = context.Request.Host.Host[..^(ConversionOrigin.Host.Length + 1)];
        var parts = label.Split('-');
        var id = parts[^1];
        if (id.Length != 32 || !Guid.TryParseExact(id, "N", out _) || (label != id && label != "w0-" + id))
        { context.Response.StatusCode = 404; return; }
        if (!sessions.TryGetValue(id, out var session) || session.Stop.IsCancellationRequested ||
            context.Request.Host.Port.GetValueOrDefault(context.Request.IsHttps ? 443 : 80) != ConversionOrigin.Port ||
            context.Request.Scheme != ConversionOrigin.Scheme)
        { context.Response.StatusCode = 404; return; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.Stop.Token);
        var token = linked.Token;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        try
        {
            if (parts.Length == 1)
            {
                await Control(context, session, token); return;
            }
            if (parts.Length != 2 || parts[0] != "w0" || session.Result.IsCompleted) { context.Response.StatusCode = 404; return; }
            await Proxy(context, session, token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or WebSocketException)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 502; else context.Abort(); }
    }
    private async Task Control(HttpContext context, Session session, CancellationToken token)
    {
        var path = context.Request.Path.Value;
        if (context.Request.Method == "GET" && path == "/__conversion/")
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self' 'unsafe-inline'; frame-src " + Origin("w0-" + session.Id) + "; connect-src 'self'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'";
            var settings = JsonSerializer.Serialize(new { session = session.Id, requestId = session.RequestId, mainOrigin = MainOrigin!.GetLeftPart(UriPartial.Authority), origin = session.Origin,
                frames = new[] { Origin("w0-" + session.Id) }, targetPath = session.Target.AbsolutePath });
            await context.Response.WriteAsync("<!doctype html><meta charset=utf-8><title>ComfyUI 转换</title><p id=status>正在加载官方 ComfyUI 前端，请保持窗口打开。</p><script>window.conversion=" + settings + ";</script><script src='/__conversion/controller.js'></script>", token); return;
        }
        if (context.Request.Method == "GET" && path == "/__conversion/controller.js")
        { context.Response.ContentType = "text/javascript"; await context.Response.WriteAsync(Resource("Controller.js"), token); return; }
        if (context.Request.Method == "GET" && path == "/__conversion/scripts")
        { await context.Response.WriteAsJsonAsync(new { guard = Resource("Guard.js"), inspect = Resource("Inspect.js"), export = Resource("Export.js") }, token); return; }
        if (context.Request.Method == "GET" && path == "/__conversion/next")
        {
            if (session.Queue.Reader.TryRead(out var work)) { await context.Response.WriteAsJsonAsync(new { work.RequestId, work.WorkerId, work.Session, work.SourceHash, work.Raw }, new JsonSerializerOptions { PropertyNamingPolicy = null }, token); return; }
            if (session.Result.IsCompleted)
            {
                await context.Response.WriteAsJsonAsync(new { done = true, success = session.Result.IsCompletedSuccessfully,
                    error = session.Result.Exception?.GetBaseException().Message ?? (session.Result.IsCanceled ? "工作流转换已取消" : null) }, token); return;
            }
            context.Response.StatusCode = 204; return;
        }
        if (context.Request.Method == "POST" && path == "/__conversion/result" && context.Request.Headers.Origin == session.Origin)
        {
            if (context.Request.ContentLength is > 16_777_216) { context.Response.StatusCode = 413; return; }
            JsonObject? data;
            try { data = await context.Request.ReadFromJsonAsync<JsonObject>(token); }
            catch (JsonException) { context.Response.StatusCode = 400; return; }
            var requestId = Text(data?["requestId"]);
            if (requestId is null || !session.Pending.TryGetValue(requestId, out var work) || Text(data!["kind"]) != "library-export" ||
                Text(data["session"]) != session.Id || data["worker"] is not JsonValue worker || !worker.TryGetValue<int>(out var workerId) || workerId != work.WorkerId || Text(data["sourceHash"]) != work.SourceHash)
            { context.Response.StatusCode = 400; return; }
            if (Text(data["error"]) is { Length: > 0 } error) work.Completion.TrySetException(new InvalidOperationException(error));
            else if (data["identityVerified"] is JsonValue identity && identity.TryGetValue<bool>(out var verified) && verified &&
                data["fullOfficialLoad"] is JsonValue load && load.TryGetValue<bool>(out var loaded) && loaded &&
                data["inspection"] is JsonObject && data["api"] is JsonObject api && api.Count > 0 &&
                api.All(pair => pair.Value is JsonObject node && !string.IsNullOrWhiteSpace(Text(node["class_type"])) && node["inputs"] is JsonObject))
                work.Completion.TrySetResult((JsonObject)api.DeepClone());
            else work.Completion.TrySetException(new InvalidOperationException("官方前端返回的 API 工作流结构或加载验证无效"));
            session.Pending.TryRemove(requestId, out _); context.Response.StatusCode = 204; return;
        }
        context.Response.StatusCode = 404;
    }
    private static string Resource(string name)
    {
        using var stream = typeof(ComfyUiConversion).Assembly.GetManifestResourceStream("ComfyUi." + name)!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string RewriteUrls(string text, Uri target, string proxyOrigin)
    {
        var authority = target.GetLeftPart(UriPartial.Authority);
        var websocket = new UriBuilder(target) { Scheme = target.Scheme == "https" ? "wss" : "ws" }.Uri.GetLeftPart(UriPartial.Authority);
        var proxyWebsocket = new UriBuilder(proxyOrigin) { Scheme = new Uri(proxyOrigin).Scheme == "https" ? "wss" : "ws" }.Uri.GetLeftPart(UriPartial.Authority);
        foreach (var pair in new[] { (authority, proxyOrigin), (websocket, proxyWebsocket) })
        {
            text = text.Replace(pair.Item1 + "/", pair.Item2 + "/", StringComparison.OrdinalIgnoreCase)
                .Replace(pair.Item1.Replace("/", "\\/") + "\\/", pair.Item2.Replace("/", "\\/") + "\\/", StringComparison.OrdinalIgnoreCase)
                .Replace(pair.Item1 + "\"", pair.Item2 + "\"", StringComparison.OrdinalIgnoreCase)
                .Replace(pair.Item1 + "'", pair.Item2 + "'", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    private static bool ReadPath(Uri uri)
    {
        var segments = Uri.UnescapeDataString(uri.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return !segments.Any(segment => segment.Equals("prompt", StringComparison.OrdinalIgnoreCase) || segment.Equals("generate", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("interrupt", StringComparison.OrdinalIgnoreCase) || segment.Equals("free", StringComparison.OrdinalIgnoreCase));
    }
    private async Task Proxy(HttpContext context, Session session, CancellationToken token)
    {
        if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS")) { context.Response.StatusCode = 405; return; }
        var target = new Uri(session.Target.GetLeftPart(UriPartial.Authority) + context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent());
        if (!ReadPath(target)) { context.Response.StatusCode = 403; return; }
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self' data: blob:; script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; frame-ancestors " + session.Origin + "; form-action 'none'; object-src 'none'; base-uri 'self'";
        if (context.WebSockets.IsWebSocketRequest)
        {
            if (!target.AbsolutePath.TrimEnd('/').EndsWith("/ws", StringComparison.Ordinal)) { context.Response.StatusCode = 403; return; }
            if (context.Request.Headers.Origin != Origin("w0-" + session.Id)) { context.Response.StatusCode = 403; return; }
            using var upstream = new ClientWebSocket();
            if (session.Http.DefaultRequestHeaders.Authorization is { } auth) upstream.Options.SetRequestHeader("Authorization", auth.ToString());
            await upstream.ConnectAsync(new UriBuilder(target) { Scheme = target.Scheme == "https" ? "wss" : "ws" }.Uri, token);
            using var browser = await context.WebSockets.AcceptWebSocketAsync();
            using var relay = CancellationTokenSource.CreateLinkedTokenSource(token);
            async Task Forward(WebSocket source, WebSocket destination)
            {
                var buffer = new byte[65536];
                while (!relay.IsCancellationRequested && source.State is WebSocketState.Open or WebSocketState.CloseSent)
                {
                    var received = await source.ReceiveAsync(buffer.AsMemory(), relay.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                            await destination.CloseOutputAsync(source.CloseStatus ?? WebSocketCloseStatus.NormalClosure, source.CloseStatusDescription, relay.Token);
                        return;
                    }
                    await destination.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, relay.Token);
                }
            }
            var downstream = Forward(upstream, browser);
            var upstreamTask = Forward(browser, upstream);
            try
            {
                await await Task.WhenAny(downstream, upstreamTask);
                relay.CancelAfter(TimeSpan.FromSeconds(2));
                await Task.WhenAll(downstream, upstreamTask);
            }
            finally
            {
                relay.Cancel();
                try { await Task.WhenAll(downstream, upstreamTask); }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            }
            return;
        }
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
            foreach (var name in new[] { "Accept", "Accept-Language", "Range", "If-None-Match", "If-Modified-Since" })
                if (context.Request.Headers.TryGetValue(name, out var value)) request.Headers.TryAddWithoutValidation(name, value.ToArray());
            using var response = await session.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = new Uri(target, location);
                if (next.GetLeftPart(UriPartial.Authority) != session.Target.GetLeftPart(UriPartial.Authority) || !ReadPath(next))
                { context.Response.StatusCode = 502; return; }
                if (next.UserInfo.Length > 0) { context.Response.StatusCode = 502; return; }
                context.Response.Headers.Location = next.PathAndQuery;
            }
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var name in new[] { "Content-Type", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified" })
            {
                if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                    context.Response.Headers[name] = values.ToArray();
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (context.Request.Method == "GET" && mediaType is "text/html" or "text/javascript" or "application/javascript" or "text/css")
            {
                var text = RewriteUrls(await response.Content.ReadAsStringAsync(token), session.Target, Origin("w0-" + session.Id));
                context.Response.ContentType = mediaType + "; charset=utf-8";
                if (mediaType == "text/html")
                {
                    var injection = "<script>" + Resource("Frame.js") + "</script>";
                    var head = text.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
                    var end = head >= 0 ? text.IndexOf('>', head) : -1;
                    text = end >= 0 ? text.Insert(end + 1, injection) : injection + text;
                }
                await context.Response.WriteAsync(text, token);
            }
            else await response.Content.CopyToAsync(context.Response.Body, token);
        }
    }
    private sealed class Exporter(Session session, int worker) : IComfyUiFrontendExporter
    {
        public async Task<JsonObject> ExportAsync(JsonObject workflow, CancellationToken cancellationToken)
        {
            var raw = workflow.ToJsonString();
            var work = new Work(Guid.NewGuid().ToString("N"), worker, session.Id, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(), raw);
            session.Pending[work.RequestId] = work;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Stop.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(6));
            try { await session.Queue.Writer.WriteAsync(work, timeout.Token); return await work.Completion.Task.WaitAsync(timeout.Token); }
            finally { session.Pending.TryRemove(work.RequestId, out _); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    public void Dispose() { cleanup?.Dispose(); foreach (var session in sessions.Values) session.Cancel(); }
}
