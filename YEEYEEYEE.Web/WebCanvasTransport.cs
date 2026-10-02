using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Web;

/// <summary>
/// WebSocket 双向传输：HostBridge.Send → 广播到所有连接的浏览器；
/// 浏览器 WebSocket 消息 → ReceiveFromBrowser → HostBridge.Receive。
/// </summary>
public sealed class WebCanvasTransport : ICanvasTransport
{
    private readonly ConcurrentDictionary<Guid, WebSocket> clients = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private Action<JsonElement>? receiveFromBrowser;
    private string? latestSceneMessage;

    public void Send(JsonElement message)
    {
        var json = message.GetRawText();
        sendLock.Wait();
        try
        {
            if (message.TryGetProperty("type", out var type)
                && type.GetString() == "host/scene.reset")
                latestSceneMessage = json;

            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var tasks = clients.Values.Select(async client =>
            {
                if (client.State == WebSocketState.Open)
                {
                    try { await client.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None); }
                    catch { /* 连接已断开 */ }
                }
            });
            Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
        finally
        {
            sendLock.Release();
        }
    }

    public void Subscribe(Action<JsonElement> handler) => receiveFromBrowser += handler;

    /// <summary>WebSocket 端点调用：新连接注册、断开移除、收到消息转发给 HostBridge。</summary>
    public async Task HandleWebSocketAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var clientId = Guid.NewGuid();
        clients[clientId] = webSocket;
        try
        {
            await sendLock.WaitAsync(cancellationToken);
            try
            {
                if (latestSceneMessage is not null && webSocket.State == WebSocketState.Open)
                {
                    var snapshot = System.Text.Encoding.UTF8.GetBytes(latestSceneMessage);
                    await webSocket.SendAsync(snapshot, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
                }
            }
            finally
            {
                sendLock.Release();
            }

            var buffer = new byte[65536];
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) break;
                var json = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                ReceiveFromBrowser(json);
            }
        }
        finally
        {
            clients.TryRemove(clientId, out _);
        }
    }

    private void ReceiveFromBrowser(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            receiveFromBrowser?.Invoke(document.RootElement.Clone());
        }
        catch { /* 忽略格式错误的消息 */ }
    }
}
