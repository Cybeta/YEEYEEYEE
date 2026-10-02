using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 一次 Agent 提交的回滚点：提交前的画布快照 + 被 Agent 改写过的文件快照。
/// 撤销时两者一起回滚，避免出现「画布退回去了、文件还留着 Agent 写的内容」。
/// </summary>
internal sealed record AgentCommitPoint(
    string CanvasPath,
    string CanvasSnapshot,
    IReadOnlyList<FileSnapshot> FileSnapshots,
    int AppliedCount,
    IReadOnlyList<string> Errors);

/// <summary>
/// 一次 Agent 提交的结果。
///
/// **为什么要有它**：提交分三段——应用动作、写画布文件、部分动作失败。
/// 只回一个 null/字符串会把「全部成功」「部分成功但已保存」「整体失败」混成一句话，
/// 用户就分不清画布到底写没写进去。这里把三段如实分开报。
/// </summary>
public sealed record AgentCommitReport(int Applied, string? Failure, IReadOnlyList<string> Errors)
{
    /// <summary>是否整体成功（应用 + 保存都完成）。</summary>
    public bool Succeeded => Failure is null;

    /// <summary>成功但有个别动作没生效时的补充说明；全部成功时为空串。</summary>
    public string PartialNote => Errors.Count == 0
        ? string.Empty
        : $"；另有 {Errors.Count} 条未生效：{string.Join("；", Errors)}";
}
