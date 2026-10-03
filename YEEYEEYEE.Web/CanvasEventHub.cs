using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Threading.Channels;

namespace YEEYEEYEE.Web;

/// <summary>一条推给客户端的消息。</summary>
internal sealed record CanvasEventMessage(string Type, string Json);

/// <summary>
/// 进程内的变更广播。
///
/// 它**只负责「告诉你变了」，不负责可靠投递**：消息进有界通道，满了就丢最旧的。
/// 理由是这类通知的性质决定的——客户端拿它当「该刷新了」的信号，真正的内容仍然靠 GET 去取。
/// 于是「丢一条」的后果只是晚一拍看到，而不是状态错；反过来，为了不丢而让慢客户端把服务端内存拖住，
/// 才是真会出事的那一头。
///
/// 为什么当初没复用那条 WebSocket 广播口（第 180 轮已随 HostBridge 一起删掉）：那条是**双向**的、
/// 协议是 <c>host/...</c> 那一套，跟「画布/编辑锁变了」不是一回事；混进去会让两种协议纠缠在一起。
/// 浏览器这边要的是单向、带会话身份、能自动重连，那正是 SSE（<c>text/event-stream</c>）的形状。
/// </summary>
internal sealed class CanvasEventHub
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly ConcurrentDictionary<Guid, Channel<CanvasEventMessage>> subscribers = new();

    public int SubscriberCount => subscribers.Count;

    /// <summary>
    /// 订阅。容量 16 的通道对「变化通知」够用——一次协作里的变更密度远低于此，
    /// 而满了说明这个客户端已经跟不上，丢最旧的正是想要的语义。
    /// </summary>
    public (Guid Id, ChannelReader<CanvasEventMessage> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<CanvasEventMessage>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var id = Guid.NewGuid();
        subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (subscribers.TryRemove(id, out var channel)) channel.Writer.TryComplete();
    }

    /// <summary>
    /// 广播。没人在听就直接返回：序列化一次也不做。
    /// 事件名与载荷平铺在同一层（<c>type</c> 与载荷字段并列），客户端只需解析一个 JSON。
    /// </summary>
    public void Publish(string type, object payload)
    {
        if (subscribers.IsEmpty) return;
        var node = JsonSerializer.SerializeToNode(payload, Options) as JsonObject ?? new JsonObject();
        node["type"] = type;
        node["at"] = DateTimeOffset.UtcNow.ToString("O");
        var message = new CanvasEventMessage(type, node.ToJsonString(Options));
        foreach (var channel in subscribers.Values) channel.Writer.TryWrite(message);
    }

    /// <summary>
    /// 画布被写入。rev 必须是**接口回报给客户端的那个修订号**（项目模式下是画布内容的哈希），
    /// 客户端拿它和自己手上的比「谁更新」——两边不是同一把尺子的话，这条推送就等于没发。
    /// </summary>
    public void CanvasChanged(long revision, string? recordId, string actor, string scope) =>
        Publish("canvas.changed", new { revision, recordId, actor, scope });

    /// <summary>编辑锁变了（申请 / 释放 / 强制接管）。载荷只给原因与是谁，列表让客户端自己去取——
    /// 一处形状两处拼，迟早会分叉。</summary>
    public void EditsChanged(string reason, string actor) =>
        Publish("edits.changed", new { reason, actor });
}
