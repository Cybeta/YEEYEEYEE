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
    private readonly PresenceRegistry presence;

    public CanvasEventHub(PresenceRegistry presence)
    {
        this.presence = presence;
    }

    public int SubscriberCount => subscribers.Count;

    /// <summary>
    /// 订阅。容量 16 的通道对「变化通知」够用——一次协作里的变更密度远低于此，
    /// 而满了说明这个客户端已经跟不上，丢最旧的正是想要的语义。
    ///
    /// <paramref name="userId"/> 为 null 表示这条连接没有用户身份（桌面桥的 Bearer 令牌不带用户）：
    /// 它照样能订阅，但**不登记在线**——「谁在线」问的是人，没有人的连接不该被算成人。
    /// 身份由调用方从会话里取（见 <c>WebEventApi</c>），注册表只认这里传进来的东西。
    /// </summary>
    public (Guid Id, ChannelReader<CanvasEventMessage> Reader) Subscribe(
        Guid? userId = null, string? displayName = null, string? client = null)
    {
        var channel = Channel.CreateBounded<CanvasEventMessage>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var id = Guid.NewGuid();

        // 先登记、先广播，**再**加进订阅表：广播不会发给刚刚订阅的这个人——他不需要知道「我上线了」，
        // 需要知道的是别人。这也让「订阅后不该凭空收到自己那条 presence.changed」自然成立。
        if (userId is { } user && client is { Length: > 0 })
        {
            var name = string.IsNullOrWhiteSpace(displayName) ? "有人" : displayName;
            if (presence.Join(user, name, client, id)) PresenceChanged("join", name);
        }

        subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        // 先移出订阅表，再注销在线：离开的这个人收不到自己的那条「下线」，别的订阅者收得到。
        if (subscribers.TryRemove(id, out var channel)) channel.Writer.TryComplete();
        if (presence.Leave(id) is { } gone && !presence.HasUser(gone.UserId))
            PresenceChanged("leave", gone.DisplayName);
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

    /// <summary>
    /// 有人上线 / 下线。载荷只给原因与是谁（与 <see cref="EditsChanged"/> 同形），在线名单本身让客户端
    /// 自己去 <c>GET /api/web/presence</c> 取——**在线 ≠ 拥有锁**，这条推送不参与任何锁或权限决策。
    /// </summary>
    public void PresenceChanged(string reason, string actor) =>
        Publish("presence.changed", new { reason, actor });
}
