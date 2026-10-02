namespace YEEYEEYEE.Web;

/// <summary>
/// 编辑范围的粒度。**按操作类型分，不是按界面分区。**
///
/// · <see cref="Node"/>：只动一个节点自己的字段（标题、正文、引用），别人改别的节点不受影响。
/// · <see cref="Tree"/>：会改变树的结构（新建、删除、移动节点，改章节归属），这类操作天然要独占一棵树。
///
/// 分成两档而不是「一律锁整棵树」，是因为整棵树锁会让「三个人各改各的章节」退化成排队。
/// </summary>
internal static class EditScope
{
    public const string Node = "node";
    public const string Tree = "tree";

    public static bool IsValid(string? value) => value is Node or Tree;
}

/// <summary>编辑锁的来源端。只影响界面文案（「某某（桌面端）正在编辑」），不参与任何权限判断。</summary>
internal static class EditClient
{
    public const string Web = "web";
    public const string Desktop = "desktop";

    public static bool IsValid(string? value) => value is Web or Desktop;

    public static string Label(string? value) => value == Desktop ? "桌面端" : "网页端";
}

/// <summary>
/// 申请的结局。**每一种「不行」都有自己的名字**，好让调用方与测试能分清是「别人拿着」「已经过期」
/// 还是「存储写不进去」——早些时候把几件事挤进同一个状态，接口回了个把调用方引向错误方向的码。
/// </summary>
internal enum EditLeaseStatus
{
    Ok,
    /// <summary>别人正持有冲突的锁。</summary>
    Conflict,
    NotFound,
    /// <summary>曾经存在，但已过期。</summary>
    Expired,
    /// <summary>这个锁存在，但不是你拿的。</summary>
    NotHolder,
    /// <summary>锁文件写不进去；不能假装成功，否则持有方以为拿到了、别人却看不到。</summary>
    StorageFailed
}

/// <summary>
/// 一次编辑租约：**谁**在**什么范围**上编辑，到什么时候为止。
///
/// 它与 <c>WorkflowNode.IsLocked</c> 是两回事：那个是**定稿保护**（文档内容的一部分，永久），
/// 这个是**会话状态**（带心跳与过期，不落进画布文件）。名字刻意不叫 Lock，避免两者混为一谈。
/// </summary>
internal sealed record EditLease(
    Guid LeaseId,
    string Scope,
    Guid? TargetId,
    Guid UserId,
    string DisplayName,
    string Client,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt);

internal sealed record EditLeaseResult(
    EditLeaseStatus Status,
    EditLease? Lease,
    string? Error,
    /// <summary>冲突时给出对方是谁，界面直接拿它显示「某某正在编辑」。</summary>
    EditLease? Holder = null,
    /// <summary>管理员强制接管时被顶掉的那些锁，用来如实告诉管理员踢掉了谁。</summary>
    IReadOnlyList<EditLease>? Displaced = null)
{
    public static EditLeaseResult Ok(EditLease lease, IReadOnlyList<EditLease>? displaced = null) =>
        new(EditLeaseStatus.Ok, lease, null, null, displaced);

    public static EditLeaseResult Fail(EditLeaseStatus status, string error, EditLease? holder = null) =>
        new(status, null, error, holder);

    /// <summary>「林晚（桌面端）」这种可读的持有者描述。</summary>
    public static string Describe(EditLease lease) =>
        $"{lease.DisplayName}（{EditClient.Label(lease.Client)}）";
}
