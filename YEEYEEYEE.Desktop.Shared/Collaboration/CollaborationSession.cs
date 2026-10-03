using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace YEEYEEYEE.Desktop;

/// <summary>服务器认下来的这个人（服务端 <c>Describe(user)</c> 的读侧投影）。</summary>
public sealed record CollaborationUser(Guid Id, string Username, string DisplayName, string Role, string RoleLabel);

/// <summary>一条编辑租约（服务端 <c>EditLease</c> 的读侧投影）。</summary>
public sealed record CollaborationLease(
    Guid LeaseId,
    string Scope,
    Guid? TargetId,
    Guid UserId,
    string DisplayName,
    string Client,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// 一次协作调用的结果。
///
/// 成功与失败都必须带一句**能直接给人看的话**，失败另外带上服务端的错误码——
/// 界面据此决定是「提示重试」「引导去登录」还是「如实转述」，不必自己拆响应体。
/// </summary>
public sealed record CollaborationResult(
    bool Ok, string Code, string Message, CollaborationLease? Holder = null, long Revision = 0)
{
    public static CollaborationResult Success(string message) => new(true, string.Empty, message);

    /// <summary>成功、并且服务端回了一个新修订号（整画布写入用：调用方拿它核对本地文件是不是同一张）。</summary>
    public static CollaborationResult Success(string message, long revision) =>
        new(true, string.Empty, message, null, revision);

    public static CollaborationResult Failure(string code, string message, CollaborationLease? holder = null) =>
        new(false, code, message, holder);
}

/// <summary>
/// 桌面端与协作服务器（YEEYEEYEE.Web）之间的**会话**：登录、退出、看谁在编辑。
///
/// 为什么桌面端走账号登录而不是桥令牌：编辑锁的意义是「显示谁在编辑」，
/// 而桌面桥那个 Bearer 令牌不带用户身份——服务端在锁接口上按设计拒绝它
/// （<c>EDIT_LEASE_REQUIRES_SESSION</c>），所以桌面端必须有自己的账号会话。
/// 声明来源端时用 <c>desktop</c>，于是别人看到的是「林晚（桌面端）正在编辑」。
///
/// 会话只活在内存里（HttpClient 的 cookie 容器）：**密码不落盘**，
/// 所以重启要重新登录。把会话持久化留到真正需要「开机就是在编辑」的时候再做。
///
/// 超时给得很短（10 秒）：服务器可能不在本机、也可能关机了，
/// 桌面端不该为它挂住；连不上就如实说连不上，画布照旧能离线用。
/// </summary>
public sealed class CollaborationSession : IDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly CookieContainer cookies = new();

    private HttpClient http;

    /// <summary>后台订阅的取消源（没在订阅时是 null）。</summary>
    private CancellationTokenSource? watchCancellation;

    /// <summary>服务器基地址（已去掉结尾斜杠）。空串表示这台机器还没配服务器。</summary>
    public string BaseUrl { get; private set; }

    /// <summary>当前登录者。未登录时为 null。</summary>
    public CollaborationUser? User { get; private set; }

    /// <summary>最近一次成功取回的编辑锁列表。</summary>
    public IReadOnlyList<CollaborationLease> Leases { get; private set; } = Array.Empty<CollaborationLease>();

    public bool IsConfigured => BaseUrl.Length > 0;

    public bool IsSignedIn => User is not null;

    public CollaborationSession(string baseUrl)
    {
        BaseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseCookies = true })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>界面上显示的名字：有显示名就用显示名，否则退回账号名。</summary>
    public static string Display(CollaborationUser user) =>
        string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;

    /// <summary>
    /// 来源端的说法（wire 值只有 <c>web</c> / <c>desktop</c>，认不出的算网页端）。
    ///
    /// 实现**只有一份**：共享层的 <see cref="UiText.ClientLabel"/>，读的是两端共读的 uiText.json。
    /// 这里留着这个方法只是不打断调用方。过去服务端 <c>EditClient.Label</c>、这一处、
    /// 网页端的 <c>locks.ts</c> 各写了一遍同样的规则，每处的注释都指着另一处——
    /// 那种「互相指着」的约定迟早会走散，而走散的表现是两端对同一把锁的称呼不一样。
    /// </summary>
    public static string ClientLabel(string client) => UiText.ClientLabel(client);

    /// <summary>
    /// 换一台服务器。**会清掉当前身份与 cookie**：换了地址之后旧会话对新服务器毫无意义，
    /// 留着它比留空更危险。设置页里改完地址要登录时先调它，应用这边持有的就是同一个会话。
    /// </summary>
    public void UseServer(string baseUrl)
    {
        var normalized = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.Equals(normalized, BaseUrl, StringComparison.Ordinal)) return;

        http.Dispose();
        http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseCookies = true })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        BaseUrl = normalized;
        Forget();
    }

    public async Task<CollaborationResult> SignInAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return CollaborationResult.Failure("NOT_CONFIGURED", "还没有填服务器地址");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return CollaborationResult.Failure("INVALID_REQUEST", "账号与密码都要填");

        try
        {
            using var response = await http.PostAsJsonAsync(
                $"{BaseUrl}/api/auth/login", new { username, password }, Options, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // 换一次账号失败时，上一次的身份必须当场作废：留着一个旧身份比留空更危险。
                Forget();
                var error = await ReadErrorAsync(response, cancellationToken);
                return CollaborationResult.Failure(error.Code ?? string.Empty, error.Message, error.Holder);
            }

            var payload = await response.Content.ReadFromJsonAsync<UserResponse>(Options, cancellationToken);
            if (payload?.User is null) return CollaborationResult.Failure("UNEXPECTED_RESPONSE", "服务器没有回身份");
            User = payload.User;
            return CollaborationResult.Success($"已登录：{Display(User)}（{User.RoleLabel}）");
        }
        catch (Exception error) when (IsTransport(error))
        {
            Forget();
            return TransportFailure(error);
        }
    }

    /// <summary>退出。**服务器不在也照样把本地会话清掉**——用户说的是「退出」。</summary>
    public async Task<CollaborationResult> SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (IsConfigured)
        {
            try
            {
                using var response = await http.PostAsync($"{BaseUrl}/api/auth/logout", content: null, cancellationToken);
            }
            catch (Exception error) when (IsTransport(error))
            {
                // 服务器已经叫不到了，本地该清还是要清。
            }
        }

        Forget();
        return CollaborationResult.Success("已退出登录");
    }

    /// <summary>取一次编辑锁列表。会话失效会当场把身份清掉，界面据此提示重新登录。</summary>
    public async Task<CollaborationResult> RefreshLeasesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return CollaborationResult.Failure("NOT_CONFIGURED", "还没有填服务器地址");
        if (!IsSignedIn) return CollaborationResult.Failure("NOT_SIGNED_IN", "先登录才能看谁在编辑");

        try
        {
            using var response = await http.GetAsync($"{BaseUrl}/api/web/edits", cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Forget();
                return CollaborationResult.Failure("UNAUTHORIZED", "登录已失效，请重新登录");
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                return CollaborationResult.Failure(error.Code ?? string.Empty, error.Message);
            }

            var payload = await response.Content.ReadFromJsonAsync<LeaseResponse>(Options, cancellationToken);
            Leases = payload?.Leases ?? Array.Empty<CollaborationLease>();
            return CollaborationResult.Success(Leases.Count == 0 ? "当前没有人正在编辑" : $"当前 {Leases.Count} 条编辑锁");
        }
        catch (Exception error) when (IsTransport(error))
        {
            return TransportFailure(error);
        }
    }

    /// <summary>
    /// 自己现在占着的那条节点锁（没占就是 null）。
    ///
    /// 桌面端只在**真的开始编辑**时才去占：点着看一圈就撒一地锁，等于把别人挡在外面而自己什么也没改。
    /// </summary>
    public CollaborationLease? HeldNodeLease { get; private set; }

    /// <summary>
    /// 占住某个节点的编辑锁。已经拿着同一个节点的锁时是幂等的（服务端把它当续期）。
    /// 别人拿着就返回失败，并把持有者放在 <see cref="CollaborationResult.Holder"/> 上——
    /// 界面照原话说「某某正在编辑」，不自己编一句。
    /// </summary>
    public async Task<CollaborationResult> AcquireNodeLeaseAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return CollaborationResult.Failure("NOT_CONFIGURED", "还没有填服务器地址");
        if (!IsSignedIn) return CollaborationResult.Failure("NOT_SIGNED_IN", "先登录才能占住编辑锁");

        try
        {
            // 来源端声明 desktop：别人看到的就是「某某（桌面端）正在编辑」。
            using var response = await http.PostAsJsonAsync(
                $"{BaseUrl}/api/web/edits",
                new { scope = "node", targetId = nodeId, client = "desktop" },
                Options, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                return CollaborationResult.Failure(error.Code ?? string.Empty, error.Message, error.Holder);
            }

            var payload = await response.Content.ReadFromJsonAsync<LeaseEnvelope>(Options, cancellationToken);
            if (payload?.Lease is null) return CollaborationResult.Failure("UNEXPECTED_RESPONSE", "服务器没有回锁");
            HeldNodeLease = payload.Lease;
            return CollaborationResult.Success("占住了这个节点");
        }
        catch (Exception error) when (IsTransport(error))
        {
            return TransportFailure(error);
        }
    }

    /// <summary>
    /// 续期。锁不在了（被别人接管、或已经过期）就把它忘掉——调用方据此**停下编辑**，
    /// 而不是接着写一个自己已经没有资格的节点。
    /// </summary>
    public async Task<CollaborationResult> RenewHeldLeaseAsync(CancellationToken cancellationToken = default)
    {
        if (HeldNodeLease is not { } held) return CollaborationResult.Success("没有需要续期的锁");

        try
        {
            using var response = await http.PutAsync($"{BaseUrl}/api/web/edits/{held.LeaseId}", content: null, cancellationToken);
            if (response.IsSuccessStatusCode) return CollaborationResult.Success("锁还在");

            var error = await ReadErrorAsync(response, cancellationToken);
            HeldNodeLease = null;
            return CollaborationResult.Failure(error.Code ?? string.Empty, error.Message);
        }
        catch (Exception error) when (IsTransport(error))
        {
            // 服务器暂时叫不到**不等于**锁没了：TTL 内还归我们，界面不必为此惊动用户。
            return CollaborationResult.Failure("NETWORK", "续期没送到（服务器暂时叫不到），锁在有效期内仍然有效");
        }
    }

    /// <summary>还回去。**失败也要忘掉本地记录**：留着它只会让界面继续显示「你在编辑」。</summary>
    public async Task<CollaborationResult> ReleaseHeldLeaseAsync(CancellationToken cancellationToken = default)
    {
        if (HeldNodeLease is not { } held) return CollaborationResult.Success("没有需要释放的锁");
        HeldNodeLease = null;
        if (!IsConfigured) return CollaborationResult.Success("已放开");

        try
        {
            using var response = await http.DeleteAsync($"{BaseUrl}/api/web/edits/{held.LeaseId}", cancellationToken);
            return response.IsSuccessStatusCode
                ? CollaborationResult.Success("已放开这个节点")
                : CollaborationResult.Failure("RELEASE_FAILED", "放开时服务器没认（锁可能已经过期）");
        }
        catch (Exception error) when (IsTransport(error))
        {
            return CollaborationResult.Failure("NETWORK", "放开没送到（服务器暂时叫不到），锁会自己过期");
        }
    }

    /// <summary>
    /// 服务端上这张画布现在的修订号；读不到就回 null 并说明原因。
    ///
    /// 这是**保存前的握手**用的：它必须等于我本地那份文件的修订号（<see cref="CanvasRevision"/>）。
    /// 相等才说明「我看的这份就是服务端那份、而且我没落后」，这时把保存交给服务端才安全。
    /// </summary>
    public async Task<(long? Revision, string Error)> CanvasRevisionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return (null, "还没有填服务器地址");
        if (!IsSignedIn) return (null, "还没有登录");

        try
        {
            using var response = await http.GetAsync($"{BaseUrl}/api/web/scene", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                return (null, error.Message);
            }

            var payload = await response.Content.ReadFromJsonAsync<SceneResponse>(Options, cancellationToken);
            return payload is null ? (null, "服务器没有回画布") : (payload.Revision, string.Empty);
        }
        catch (Exception error) when (IsTransport(error))
        {
            return (null, TransportFailure(error).Message);
        }
    }

    /// <summary>
    /// 把整张画布交给服务端保存（<c>PUT /api/web/canvas</c>，请求体就是画布字节）。
    ///
    /// <paramref name="baseRevision"/> 是我手上那份的修订号：服务端拿它做 CAS，
    /// 别人在这之间改过就会被拒（<c>SCENE_REVISION_CONFLICT</c>），而不是把别人的改动盖掉。
    /// 整画布写入是结构级操作，服务端那边要整棵树锁——别人占着任何节点都会回 <c>EDIT_CONFLICT</c>，
    /// 持有者放在 <see cref="CollaborationResult.Holder"/> 上。
    /// </summary>
    public async Task<CollaborationResult> SaveCanvasAsync(
        long baseRevision, byte[] canvas, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return CollaborationResult.Failure("NOT_CONFIGURED", "还没有填服务器地址");
        if (!IsSignedIn) return CollaborationResult.Failure("NOT_SIGNED_IN", "先登录才能把保存交给服务端");

        try
        {
            // 来源端声明 desktop：别人看到的就是「某某（桌面端）正在编辑」。
            var content = new ByteArrayContent(canvas);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using var response = await http.PutAsync(
                $"{BaseUrl}/api/web/canvas?baseRevision={baseRevision}&client=desktop", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                return CollaborationResult.Failure(error.Code ?? string.Empty, error.Message, error.Holder);
            }

            var payload = await response.Content.ReadFromJsonAsync<CanvasWriteResponse>(Options, cancellationToken);
            return payload is null
                ? CollaborationResult.Failure("UNEXPECTED_RESPONSE", "服务器没有回修订号")
                : CollaborationResult.Success($"已保存到服务端（修订 {payload.Revision}，{payload.Nodes} 个节点）", payload.Revision);
        }
        catch (Exception error) when (IsTransport(error))
        {
            return TransportFailure(error);
        }
    }

    /// <summary>
    /// 起一个后台订阅（重复调用只起一个；没登录就什么都不做）。
    ///
    /// 退出登录、会话失效时它会**自己停下**——这一点必须有：服务端只在订阅那一刻校验过身份，
    /// 不主动断开的话，退出之后那条流还开着，桌面端会继续收到「别人改了画布」。
    /// 回调跑在后台线程上，界面自己负责切回 UI 线程。
    /// </summary>
    public void StartWatching(Action<CanvasChangedNotice> onCanvasChanged, Action onEditsChanged)
    {
        if (watchCancellation is not null || !IsSignedIn) return;
        var cancellation = new CancellationTokenSource();
        watchCancellation = cancellation;
        _ = Task.Run(() => WatchAsync(onCanvasChanged, onEditsChanged, cancellation.Token));
    }

    /// <summary>
    /// 订阅服务端的变更推送，直到取消。**断线自己重连**（退避 3 秒）：
    /// 浏览器的 <c>EventSource</c> 会按服务端给的 <c>retry</c> 自己回来，HttpClient 读流不会。
    /// </summary>
    public async Task WatchAsync(
        Action<CanvasChangedNotice> onCanvasChanged, Action onEditsChanged, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var response = await http.GetAsync(
                    $"{BaseUrl}/api/web/events", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // 会话已经失效：别再空转，把身份清掉，让界面提示重新登录。
                    Forget();
                    return;
                }

                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var reader = new StreamReader(stream);
                    await ReadFramesAsync(reader, onCanvasChanged, onEditsChanged, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (IsTransport(error))
            {
                // 服务器暂时不在（重启、断网）：退避之后再来。
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// 读帧：**空行才算一帧读完**；<c>:</c> 开头是心跳、<c>retry:</c> 是重连提示，都不是事件。
    /// 与服务端写帧的格式一一对应（那边写 <c>event:</c> + <c>data:</c> + 空行）。
    /// </summary>
    private static async Task ReadFramesAsync(
        StreamReader reader, Action<CanvasChangedNotice> onCanvasChanged, Action onEditsChanged,
        CancellationToken cancellationToken)
    {
        var type = (string?)null;
        var data = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) return;   // 流关了：交给外层退避重连
            if (line.Length == 0)
            {
                if (type is not null)
                {
                    if (CollaborationEvents.ParseCanvasChanged(type, data.ToString()) is { } notice) onCanvasChanged(notice);
                    else if (CollaborationEvents.IsEditsChangedFrame(type, data.ToString())) onEditsChanged();
                }

                type = null;
                data.Clear();
                continue;
            }

            if (line[0] == ':') continue;
            if (line.StartsWith("event:", StringComparison.Ordinal)) type = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(line[5..].Trim());
        }
    }

    /// <summary>会话与身份一起清掉（cookie 也要丢：留着过期 cookie 只会让下一次请求白跑）。</summary>
    private void Forget()
    {
        User = null;
        Leases = Array.Empty<CollaborationLease>();
        // 身份没了，手上那条锁也就不再代表任何人了：本地记录必须跟着清。
        HeldNodeLease = null;
        // 订阅也要停：服务端只在订阅那一刻校验过身份，不主动断开的话退出之后它还在推。
        watchCancellation?.Cancel();
        watchCancellation?.Dispose();
        watchCancellation = null;
        // 逐个置为过期，而不是换一个容器：容器是 HttpClientHandler 建的时候拿走的，
        // 换字段只是换了我们手上的引用，请求照样会带着旧 cookie 出去。
        foreach (Cookie cookie in cookies.GetAllCookies()) cookie.Expired = true;
    }

    private static bool IsTransport(Exception error) =>
        error is HttpRequestException or TaskCanceledException or OperationCanceledException;

    private static CollaborationResult TransportFailure(Exception error) =>
        error is TaskCanceledException or OperationCanceledException
            ? CollaborationResult.Failure("TIMEOUT", "服务器没有在 10 秒内回应（地址对吗？服务起着吗？）")
            : CollaborationResult.Failure("NETWORK", "连不上服务器：" + error.Message);

    /// <summary>
    /// 服务端的错误契约是 <c>{ code, message }</c>，锁冲突时另外带 <c>holder</c>。
    /// 这里**只读一次响应体**：读完就取不到了，所以把需要的字段一起带回来，
    /// 调用方再按需取（冲突时才有 holder，其余是 null）。
    /// </summary>
    private static async Task<ErrorResponse> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>(Options, cancellationToken);
            if (payload is not null && !string.IsNullOrWhiteSpace(payload.Message)) return payload;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or HttpRequestException)
        {
        }

        return new ErrorResponse($"HTTP_{(int)response.StatusCode}", $"服务器回了 {(int)response.StatusCode}，没给原因", null);
    }

    public void Dispose()
    {
        watchCancellation?.Cancel();
        watchCancellation?.Dispose();
        watchCancellation = null;
        http.Dispose();
    }

    private sealed record UserResponse(CollaborationUser? User);

    /// <summary>场景接口的读侧：这一路只关心修订号（别的字段用不上）。</summary>
    private sealed record SceneResponse(long Revision);

    /// <summary>整画布写入的响应：新修订号与节点数。</summary>
    private sealed record CanvasWriteResponse(long Revision, int Nodes);

    private sealed record LeaseResponse(CollaborationLease[]? Leases);

    /// <summary>占锁的响应：<c>{ lease, displaced }</c>。</summary>
    private sealed record LeaseEnvelope(CollaborationLease? Lease);

    private sealed record ErrorResponse(string? Code, string Message, CollaborationLease? Holder);
}
