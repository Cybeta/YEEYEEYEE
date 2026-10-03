namespace YEEYEEYEE.Web;

/// <summary>
/// 变更推送：<c>GET /api/web/events</c>（SSE，<c>text/event-stream</c>）。
///
/// 为什么是 SSE 而不是 WebSocket：这条流**只有服务端往客户端说话**，而 SSE 正好是这个形状——
/// 一个普通的 GET、靠会话 cookie 带身份、断线由浏览器（<c>EventSource</c>）自己按 <c>retry</c> 重连。
/// 用 WebSocket 就得自己写帧、心跳与重连，换不来任何东西。桌面端将来接同一路也容易，
/// 一个 HttpClient 读流而已。
///
/// 推送的语义是「**告诉你变了**」，不是投递数据：载荷里只有修订号、记录号与是谁，
/// 真内容仍然靠 GET 取。这样丢一条的后果只是晚一拍，而不是状态错——
/// 具体到编辑锁，锁列表只有一处形状（<c>GET /api/web/edits</c>），不在这里再拼一份。
/// </summary>
internal static class WebEventApi
{
    /// <summary>心跳间隔。注释行不进 onmessage，客户端不用为它写分支，但足够让中间设备知道连接还活着。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/web/events", async (HttpContext context, CanvasEventHub hub) =>
        {
            // 这条流会说出「谁在编辑、谁改了什么」，所以不能匿名读。
            // 权限是统一的判据：登录用户按角色拿（只读账号也有读权限），桌面桥按服务端配置的 claim。
            if (WebAccessGuard.Permissions(context).Count == 0)
                return Results.Json(
                    new { code = "EVENTS_REQUIRE_SESSION", message = "变更推送要登录后才能订阅" },
                    statusCode: StatusCodes.Status403Forbidden);

            context.Response.Headers.CacheControl = "no-cache";
            // 反代（nginx / 各种容器网关）默认会把响应缓冲起来，那样推送就变成了「攒一批再发」。
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            // 重连间隔由服务端指定：EventSource 断了自己回来。
            await context.Response.WriteAsync("retry: 3000\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);

            var (id, reader) = hub.Subscribe();
            try
            {
                var pending = reader.ReadAsync(context.RequestAborted).AsTask();
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    var beat = Task.Delay(HeartbeatInterval, context.RequestAborted);
                    if (await Task.WhenAny(pending, beat) == pending)
                    {
                        var message = await pending;
                        await context.Response.WriteAsync($"event: {message.Type}\ndata: {message.Json}\n\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        pending = reader.ReadAsync(context.RequestAborted).AsTask();
                    }
                    else
                    {
                        // 等这条 Delay 落地（或被取消），别把它丢在那儿不管——不观察的异常任务会被记进日志。
                        try { await beat; } catch (OperationCanceledException) { break; }
                        await context.Response.WriteAsync(": ping\n\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 客户端断开是正常收尾，不是错误。
            }
            finally
            {
                hub.Unsubscribe(id);
            }
            return Results.Empty;
        });
    }
}
