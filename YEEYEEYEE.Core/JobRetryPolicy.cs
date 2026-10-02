namespace YEEYEEYEE.Core;

/// <summary>
/// 任务重试策略（目标 5 / 失败重试）：
/// 只有终态的失败或已取消任务可以重试，且同一次输入的反复尝试有次数上限，
/// 避免在同一个错误上无限重试；超过上限后只能由人手工重新发起。
/// </summary>
public static class JobRetryPolicy
{
    /// <summary>默认上限：首次 + 2 次重试，共 3 次尝试。</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>该状态是否属于可重试的终态。</summary>
    public static bool IsRetryableState(JobState state) => state is JobState.Failed or JobState.Cancelled;

    /// <summary>
    /// 能否重试：终态 + 未超上限 + 记录了调用信息。**只看这一个任务**，用于「单条任务能不能重试」的旧口径；
    /// 判断一条尝试链整体还能不能再重试请用 <see cref="PlanRetry"/>——只看源任务的 Attempt 会被绕过：
    /// 反复重试同一个失败的历史源，每次都算出第 2 次尝试，永远够不到上限。
    /// 不能重试时给出可读原因，供界面与协议错误直接展示。
    /// </summary>
    public static bool CanRetry(Job? job, int maxAttempts, out string reason)
    {
        reason = string.Empty;
        if (job is null) { reason = "Job 不存在"; return false; }
        var limit = Normalize(maxAttempts);
        if (!IsRetryableState(job.State))
        {
            reason = $"只有失败或已取消的任务可以重试（当前状态 {job.State}）";
            return false;
        }

        if (job.Attempt >= limit)
        {
            reason = $"已达到重试上限（{limit} 次尝试），请检查问题后手工重新发起";
            return false;
        }

        if (string.IsNullOrWhiteSpace(job.Tool))
        {
            reason = "该任务没有记录调用信息，无法重试";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 按**整条尝试链**决定下一次尝试号（目标 5 返工 R1）。三条规则缺一不可：
    /// · 同根已经成功过：再重试就是重复执行同一次输入，直接拒绝；
    /// · 同根已有进行中的尝试：并发重试会让同一次输入同时跑两份，等它结束再说；
    /// · 尝试号按链上**已用过的最大号**递增，而不是源任务号 + 1——否则反复重试历史源会永远停在第 2 次，
    ///   绕过总上限（这正是返工前被复核指出的绕过路径）。
    /// </summary>
    public static bool PlanRetry(
        IReadOnlyList<Job>? chain,
        Job? source,
        int maxAttempts,
        out int nextAttempt,
        out string reason)
    {
        nextAttempt = 0;
        reason = string.Empty;
        if (source is null) { reason = "Job 不存在"; return false; }

        var limit = Normalize(maxAttempts);
        var attempts = chain is null || chain.Count == 0 ? new[] { source } : chain.ToArray();

        if (attempts.Any(job => job.State == JobState.Succeeded))
        {
            reason = "同一根任务已有成功的尝试，重试会重复执行同一次输入：请改用「按相同参数重新发起」";
            return false;
        }

        var active = attempts.FirstOrDefault(job => job.State is JobState.Queued or JobState.Running or JobState.Cancelling);
        if (active is not null)
        {
            reason = $"同一根任务已有进行中的尝试（第 {active.Attempt} 次，状态 {active.State}），请等它结束";
            return false;
        }

        if (!IsRetryableState(source.State))
        {
            reason = $"只有失败或已取消的任务可以重试（当前状态 {source.State}）";
            return false;
        }

        if (string.IsNullOrWhiteSpace(source.Tool))
        {
            reason = "该任务没有记录调用信息，无法重试";
            return false;
        }

        var used = attempts.Max(job => job.Attempt);
        if (used >= limit)
        {
            reason = $"已达到重试上限（{limit} 次尝试），请检查问题后手工重新发起";
            return false;
        }

        nextAttempt = used + 1;
        return true;
    }

    /// <summary>把上限规整到至少 1 次尝试。</summary>
    public static int Normalize(int maxAttempts) => maxAttempts < 1 ? 1 : maxAttempts;

    /// <summary>给界面看的尝试描述，例如「第 2 次尝试（共最多 3 次）」。</summary>
    public static string DescribeAttempt(Job job, int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(job);
        return $"第 {job.Attempt} 次尝试（共最多 {Normalize(maxAttempts)} 次）";
    }
}
