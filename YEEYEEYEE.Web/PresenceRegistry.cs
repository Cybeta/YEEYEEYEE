using System.Collections.Concurrent;

namespace YEEYEEYEE.Web;

/// <summary>一条活着（或刚断）的连接：谁、走哪个端、什么时候还活跃过。**连接的身份是它自己的 id**，
/// 不是 userId——同一个人开两个标签就是两条连接，聚合发生在 <see cref="PresenceRegistry.List"/> 里。</summary>
internal sealed record PresenceConnection(
    Guid ConnectionId,
    Guid UserId,
    string DisplayName,
    string Client,
    DateTimeOffset LastSeenAt);

/// <summary>按 userId 聚合后的一个「人」：显示名、他用的端、连接数、最后活跃时间。
/// <see cref="Clients"/> 去重（同一个人网页端 + 桌面端各开一个，这里就是 web 与 desktop）。</summary>
internal sealed record PresenceEntry(
    Guid UserId,
    string DisplayName,
    IReadOnlyList<string> Clients,
    int Connections,
    DateTimeOffset LastSeenAt);

/// <summary>
/// 进程内的「谁在线」登记处。**只回答「这个人此刻连着我们」**，不参与任何锁或权限判断——
/// 锁是目标级的（谁正在编辑哪个节点），在线是人级的（谁有活连接），两件事各有各的依据。
///
/// 为什么放内存而不是数据库：在线是**易失的当下状态**，进程重启后就该从零开始；
/// 落盘只会留下「重启前在线」这种解释不清的记录。会话表那边的 <c>last_seen_at</c> 是另一条依据，
/// 由接口层合并进来做兜底，不在这里混成一个「在线」。
///
/// 连接由 <see cref="CanvasEventHub"/> 在 SSE 订阅 / 断开时登记与注销（见 <c>WebEventApi</c>），
/// 所以「活连接」与「在线」在这里是同一个事实的两个说法。
/// </summary>
internal sealed class PresenceRegistry
{
    /// <summary>默认 2 分钟：与编辑锁同一档。太久会把刚断线的人一直算成在线，太短则一次短暂抖动就闪一下。</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<Guid, PresenceConnection> connections = new();

    /// <summary>
    /// 登记一条连接。返回**这个人之前是不是不在线**——用来决定要不要广播「有人上线」。
    /// 同一个人开第二个连接不算新上线，返回 false。
    /// </summary>
    public bool Join(Guid userId, string displayName, string client, Guid connectionId)
    {
        var isNewPerson = !HasUser(userId);
        connections[connectionId] = new PresenceConnection(
            connectionId, userId, displayName, client, DateTimeOffset.UtcNow);
        return isNewPerson;
    }

    /// <summary>
    /// 注销一条连接，返回被移除的那一条（没登记过就是 null）。调用方拿它判断「这是不是这个人的最后一条」。
    /// </summary>
    public PresenceConnection? Leave(Guid connectionId) =>
        connections.TryRemove(connectionId, out var removed) ? removed : null;

    /// <summary>某个人现在还有没有活连接。**不套 TTL**：上线/下线是连接数瞬间变的，不是等过期算出来的。</summary>
    public bool HasUser(Guid userId) => connections.Values.Any(connection => connection.UserId == userId);

    /// <summary>把这个人的所有连接都标记成刚活跃（SSE 心跳时调用）。</summary>
    public void Touch(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var connection in connections.Values)
        {
            if (connection.UserId != userId) continue;
            connections.TryUpdate(connection.ConnectionId, connection with { LastSeenAt = now }, connection);
        }
    }

    /// <summary>
    /// 按 userId 聚合出当前在线的人。只有 <c>LastSeenAt</c> 在 <paramref name="ttl"/> 内的连接才算——
    /// 这是万一 <c>RequestAborted</c> 没触发的兜底，不是主路径（主路径是断开时显式注销）。
    /// </summary>
    public IReadOnlyList<PresenceEntry> List(TimeSpan ttl)
    {
        var now = DateTimeOffset.UtcNow;
        return connections.Values
            .Where(connection => now - connection.LastSeenAt <= ttl)
            .GroupBy(connection => connection.UserId)
            .Select(group => new PresenceEntry(
                group.Key,
                group.OrderByDescending(connection => connection.LastSeenAt).First().DisplayName,
                group.Select(connection => connection.Client)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(client => client == EditClient.Web ? 0 : 1)
                    .ToList(),
                group.Count(),
                group.Max(connection => connection.LastSeenAt)))
            .OrderBy(entry => entry.DisplayName, StringComparer.Ordinal)
            .ThenBy(entry => entry.UserId)
            .ToList();
    }
}
