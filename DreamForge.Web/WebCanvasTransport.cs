using System.Text.Json;
using DreamForge.Host;

namespace DreamForge.Web;

// 浏览器 postMessage 适配器的最小边界；具体 JS 互操作由 Web UI 项目提供。
public sealed class WebCanvasTransport : ICanvasTransport
{
    private readonly Action<string> postMessage;
    private Action<JsonElement>? receiveFromBrowser;

    public WebCanvasTransport(Action<string> postMessage) => this.postMessage = postMessage;
    public void Send(JsonElement message) => postMessage(message.GetRawText());
    public void Subscribe(Action<JsonElement> handler) => receiveFromBrowser += handler;
    public void ReceiveFromBrowser(string json)
    {
        using var document = JsonDocument.Parse(json);
        receiveFromBrowser?.Invoke(document.RootElement.Clone());
    }
}
