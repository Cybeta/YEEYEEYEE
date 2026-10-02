namespace YEEYEEYEE.Web.Auth;

/// <summary>
/// 用户的角色。**数值即权限高低**，比较直接用它。
///
/// · <see cref="Viewer"/> 只能看：能读场景与资产、能看技能与任务，改不了任何东西。
/// · <see cref="Editor"/> 能改画布、能调技能。
/// · <see cref="Admin"/> 在 Editor 之上，多一项「管用户」。
///
/// 角色的权限在服务端换算成 claims 再判，不由前端传——前端传什么都会被忽略。
/// </summary>
public enum UserRole
{
    Viewer = 0,
    Editor = 1,
    Admin = 2
}

public static class UserRoles
{
    public const string Viewer = "Viewer";
    public const string Editor = "Editor";
    public const string Admin = "Admin";

    public static string ToText(UserRole role) => role switch
    {
        UserRole.Admin => Admin,
        UserRole.Editor => Editor,
        _ => Viewer
    };

    /// <summary>文本 → 角色。认不出来的一律按最低权限处理，不抛异常、也不默认放权。</summary>
    public static UserRole Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "admin" => UserRole.Admin,
        "editor" => UserRole.Editor,
        _ => UserRole.Viewer
    };

    public static string Label(UserRole role) => role switch
    {
        UserRole.Admin => "管理员",
        UserRole.Editor => "编辑",
        _ => "只读"
    };

    /// <summary>这一角色在这个服务里被授予的权限。**唯一一处角色 → 权限的换算。**</summary>
    public static IReadOnlySet<string> ClaimsFor(UserRole role) => role switch
    {
        UserRole.Admin => All,
        UserRole.Editor => All,
        _ => Empty
    };

    private static readonly HashSet<string> All = new(new[] { "canvas.edit", "skill.invoke", "job.cancel" }, StringComparer.Ordinal);
    private static readonly HashSet<string> Empty = new(StringComparer.Ordinal);
}

/// <summary>一个用户（**不含口令哈希**，用于返回给界面；哈希只在库里流转）。</summary>
public sealed record WebUser(
    Guid Id,
    string Username,
    string DisplayName,
    UserRole Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    bool Disabled);

/// <summary>
/// 写操作的结果。业务上的「不行」不抛异常，靠它如实回给调用方。
/// **每一种「不行」都有自己的名字**——早些时候把「当前口令不对」也塞进 <see cref="NotFound"/>，
/// 结果接口回了 404「用户不存在」，把调用方引向完全错误的方向。
/// </summary>
public enum UserWriteStatus
{
    Ok,
    UsernameTaken,
    InvalidUsername,
    WeakPassword,
    LastAdmin,
    NotFound,
    SetupAlreadyDone,
    /// <summary>用户名或口令不对。二者共用一条，不区分——区分等于告诉对方哪些用户名存在。</summary>
    InvalidCredentials,
    AccountDisabled,
    /// <summary>改口令时给错了当前口令。</summary>
    WrongPassword
}

public sealed record UserWriteResult(UserWriteStatus Status, WebUser? User, string? Error)
{
    public static UserWriteResult Ok(WebUser user) => new(UserWriteStatus.Ok, user, null);
    public static UserWriteResult Fail(UserWriteStatus status, string error) => new(status, null, error);
}
