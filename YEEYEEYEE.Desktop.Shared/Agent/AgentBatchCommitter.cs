namespace YEEYEEYEE.Desktop;

/// <summary>一批动作提交到了哪一步。</summary>
public enum AgentCommitStage
{
    /// <summary>账本拒绝（重复提交、批次无效等），什么都没做。</summary>
    Rejected,

    /// <summary>动作应用阶段就有失败：没有进入保存。</summary>
    ActionFailed,

    /// <summary>动作已应用，但保存失败：画布文件仍是上一次的内容。</summary>
    SaveFailed,

    /// <summary>动作全部应用且保存成功。</summary>
    Saved
}

/// <summary>
/// 一次批次提交的分阶段结果（返工 R2/R3）。界面与调用方**只能照这个结果说话**：
/// · <see cref="Succeeded"/> 为真才算「已保存」；
/// · <see cref="ActionsApplied"/> 表示画布已被改动（可能有部分动作没成功，此时 <see cref="ActionErrors"/> 非空）；
/// · <see cref="CanRetrySave"/> 为真表示只需重存一次，不必重新应用动作（避免重复副作用）。
/// </summary>
public sealed record AgentCommitResult(
    AgentCommitStage Stage,
    Guid BatchId,
    int AppliedCount,
    IReadOnlyList<string> ActionErrors,
    string Message)
{
    public bool Succeeded => Stage == AgentCommitStage.Saved;

    /// <summary>画布是否已经被这批动作改动过（部分成功也算，因为改动真实存在、需要撤销记录）。</summary>
    public bool ActionsApplied => Stage is AgentCommitStage.Saved or AgentCommitStage.SaveFailed;

    /// <summary>动作部分成功：画布被改了，但不是全部动作都成功。</summary>
    public bool Partial => Stage == AgentCommitStage.ActionFailed && AppliedCount > 0;

    /// <summary>只需重存，不必重新应用动作。</summary>
    public bool CanRetrySave => Stage == AgentCommitStage.SaveFailed;

    /// <summary>画布被改动过、需要给用户留下撤销记录。</summary>
    public bool NeedsRecoveryRecord => ActionsApplied;
}

/// <summary>
/// Agent 批次提交的唯一入口（目标 5 返工 R2/R3）。把提交拆成明确的阶段并逐段如实返回：
/// 账本准入 → 动作应用（单动作失败即回退该动作的修改）→ 保存 → 账本落定。
///
/// 三条硬语义：
/// · **只有「动作全部成功 + 保存成功」才算提交完成**：部分应用、保存失败都不报成功（返工前保存失败被吞掉）；
/// · **失败可恢复且不重复副作用**：保存失败时同一批次号可以只重试保存（<c>saveOnly</c>），不必再应用一次动作；
/// · **账本与结果一致**：Succeeded 之后任何重复提交都被拒；Failed 之后普通重复提交也被拒（避免悄悄重放），
///   只有显式的续跑才放行。
///
/// 它不碰窗体，副作用由调用方以委托注入，因此可以在没有 UI 的情况下做真实提交路径的回归。
/// </summary>
public sealed class AgentBatchCommitter
{
    private readonly AgentCommitLedger ledger;

    public AgentBatchCommitter(AgentCommitLedger? ledger = null) => this.ledger = ledger ?? new AgentCommitLedger();

    /// <summary>本提交入口使用的账本（供诊断与回归检查）。</summary>
    public AgentCommitLedger Ledger => ledger;

    /// <summary>
    /// 提交一批动作。<paramref name="apply"/> 负责把动作落到画布并返回每条动作的结果；
    /// <paramref name="save"/> 负责落盘并返回真实成败（不得吞掉失败）。
    /// <paramref name="saveOnly"/> 为真是「动作已经应用过、只重存一次」的续跑路径。
    /// </summary>
    public AgentCommitResult Commit(
        Guid? batchId,
        IReadOnlyList<AgentAction> actions,
        Func<AgentApplyResult> apply,
        Func<bool> save,
        bool saveOnly = false,
        string? canvasKey = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(save);

        var batch = batchId ?? AgentCommitLedger.NewBatchId();

        // 返工 S2：这一批动作已经落画、只是上次没存下来 → 普通「保存」也自动走只重存，绝不重放动作。
        // 否则用户拒绝一次即时重存之后会被 Failed 状态卡死（旧实现在这里直接拒绝普通提交）。
        if (!saveOnly
            && ledger.StateOf(batch) == AgentBatchState.Failed
            && ledger.FailureOf(batch) == AgentBatchFailure.Save)
            saveOnly = true;

        if (saveOnly)
        {
            if (!ledger.TryResume(batch, canvasKey, out var resumeReason))
                return new AgentCommitResult(AgentCommitStage.Rejected, batch, 0, Array.Empty<string>(), resumeReason);
            return SavePhase(batch, 0, Array.Empty<string>(), save);
        }

        if (actions.Count == 0)
            return new AgentCommitResult(AgentCommitStage.Rejected, batch, 0, Array.Empty<string>(), "这一批没有可提交的动作。");

        if (!ledger.TryBegin(batch, canvasKey, out var reason))
            return new AgentCommitResult(AgentCommitStage.Rejected, batch, 0, Array.Empty<string>(), reason);

        AgentApplyResult applied;
        try
        {
            applied = apply();
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ledger.MarkFailed(batch, AgentBatchFailure.Apply);
            var errors = new[] { error.Message };
            return new AgentCommitResult(AgentCommitStage.ActionFailed, batch, 0, errors,
                "动作没有应用成功，画布保持原样：" + Environment.NewLine + error.Message);
        }

        if (applied.Errors.Count > 0)
        {
            ledger.MarkFailed(batch, AgentBatchFailure.Apply);
            var message = applied.Applied == 0
                ? $"这批 {actions.Count} 条动作都没能应用，画布保持原样。"
                : $"这批动作只有 {applied.Applied}/{actions.Count} 条应用成功：画布已被改动，失败的条目请修正后重来；"
                    + "已应用的部分可用「撤销本批」回退。";
            return new AgentCommitResult(AgentCommitStage.ActionFailed, batch, applied.Applied, applied.Errors, message);
        }

        return SavePhase(batch, applied.Applied, applied.Errors, save);
    }

    /// <summary>保存阶段：成功才算提交完成，失败保留可重试语义。</summary>
    private AgentCommitResult SavePhase(Guid batch, int appliedCount, IReadOnlyList<string> actionErrors, Func<bool> save)
    {
        bool saved;
        try
        {
            saved = save();
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            saved = false;
            actionErrors = actionErrors.Concat(new[] { "保存时出错：" + error.Message }).ToArray();
        }

        if (!saved)
        {
            ledger.MarkFailed(batch, AgentBatchFailure.Save);
            return new AgentCommitResult(AgentCommitStage.SaveFailed, batch, appliedCount, actionErrors,
                "动作已应用到画布，但**没有保存成功**：画布文件仍是上一次的内容。"
                + Environment.NewLine + "可以点「重试保存」只重存一次（不会重复应用动作），或点「撤销本批」回退已应用的改动。");
        }

        ledger.MarkSucceeded(batch);
        return new AgentCommitResult(AgentCommitStage.Saved, batch, appliedCount, actionErrors, "已提交并保存。");
    }
}
