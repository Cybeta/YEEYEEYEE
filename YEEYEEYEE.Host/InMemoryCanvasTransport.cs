using System.Text.Json;

namespace YEEYEEYEE.Host;

public sealed class InMemoryCanvasTransport : ICanvasTransport
{
    private Action<JsonElement>? handler;
    public List<JsonElement> Sent { get; } = [];
    public void Send(JsonElement message) => Sent.Add(message.Clone());
    public void Subscribe(Action<JsonElement> handler) => this.handler += handler;
    public void Deliver(JsonElement message) => handler?.Invoke(message);
}
