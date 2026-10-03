using System.Net;
using System.Net.Http.Json;
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
public sealed record CollaborationResult(bool Ok, string Code, string Message)
{
    public static CollaborationResult Success(string message) => new(true, string.Empty, message);

    public static CollaborationResult Failure(string code, string message) => new(false, code, message);
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

    private readonly HttpClient http;
    private readonly CookieContainer cookies = new();

    /// <summary>服务器基地址（已去掉结尾斜杠）。空串表示这台机器还没配服务器。</summary>
    public string BaseUrl { get; }

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
                var (code, message) = await ReadErrorAsync(response, cancellationToken);
                return CollaborationResult.Failure(code, message);
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
                var (code, message) = await ReadErrorAsync(response, cancellationToken);
                return CollaborationResult.Failure(code, message);
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

    /// <summary>会话与身份一起清掉（cookie 也要丢：留着过期 cookie 只会让下一次请求白跑）。</summary>
    private void Forget()
    {
        User = null;
        Leases = Array.Empty<CollaborationLease>();
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

    /// <summary>服务端的错误契约是 <c>{ code, message }</c>；解不开就如实说解不开，不编一句。</summary>
    private static async Task<(string Code, string Message)> ReadErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>(Options, cancellationToken);
            if (payload is not null && !string.IsNullOrWhiteSpace(payload.Message))
                return (payload.Code ?? string.Empty, payload.Message);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or HttpRequestException)
        {
        }

        return ($"HTTP_{(int)response.StatusCode}", $"服务器回了 {(int)response.StatusCode}，没给原因");
    }

    public void Dispose() => http.Dispose();

    private sealed record UserResponse(CollaborationUser? User);

    private sealed record LeaseResponse(CollaborationLease[]? Leases);

    private sealed record ErrorResponse(string? Code, string? Message);
}
