namespace DreamForge.Desktop;

/// <summary>一批动作在账本里的状态。</summary>
public enum AgentBatchState
{
    /// <summary>已开始提交、还没落定（提交中）。</summary>
    Pending,

    /// <summary>动作全部应用且已保存。</summary>
    Succeeded,

    /// <summary>提交失败（动作没全应用，或保存失败）。仍可「重试保存」或整批重来。</summary>
    Failed,

    /// <summary>
    /// 撤销只完成了一部分（返工 U2）：画布可能已回退、文件或资产没恢复。
    /// 这时**不允许**再按原来的保存失败去「只重存」——那会把已回退的画布当成这批动作的结果存下来。
    /// 必须先把恢复做完（重试撤销），或在恢复完成后明确重新提交。
    /// </summary>
    NeedsRecovery,

    /// <summary>已整批回滚：画布与副作用都回到提交前，允许用同一批次号重新提交。</summary>
    RolledBack
}

/// <summary>
/// 失败发生在哪一段（返工 S2）。区分它是因为恢复路径不同：
/// 动作没落画 → 重来要重新应用；动作已落画、只是没存下来 → 只能重存，**绝不能重放动作**。
/// </summary>
public enum AgentBatchFailure
{
    /// <summary>没有失败记录。</summary>
    None,

    /// <summary>动作应用阶段失败（可能有部分动作已应用）。</summary>
    Apply,

    /// <summary>动作已应用，保存失败。</summary>
    Save,

    /// <summary>撤销/恢复没有完全成功（返工 U2）。</summary>
    Rollback
}

/// <summary>
/// Agent 批次提交账本（目标 5 / 审批与自动模式各自只提交一次；返工 R2 改为分阶段状态）。
///
/// 为什么不能只记「消费过」：提交要分「动作应用 → 保存」两段，任一段失败都不算完成。
/// 只记「消费过」会让失败的那次也无法重来（用户被卡死）；不记又会让双击/重复确认把同一批应用两次。
/// 因此账本记状态：
/// · 第一次提交 → Pending；
/// · 全部应用 + 保存成功 → Succeeded（此后任何重复提交一律拒绝）；
/// · 中途失败 → Failed（普通重复提交仍拒绝，但允许显式「重试保存」用同一批次号续跑）。
///
/// 用批次号而不是动作内容指纹做键：内容相同的新提案是合法的新批次，不会被误拒。
/// </summary>
public sealed class AgentCommitLedger
{
    /// <summary>保留最近记过的批次号数量；只用于防重复，不需要无限增长。</summary>
    private const int KeepRecent = 256;

    private readonly Dictionary<Guid, AgentBatchState> states = new();
    private readonly Dictionary<Guid, AgentBatchFailure> failures = new();
    /// <summary>每批提交时所在的画布身份（返工 V3）：跨画布续跑/重存会把 A 的动作记成 B 的结果。</summary>
    private readonly Dictionary<Guid, string> canvasKeys = new();
    private readonly Queue<Guid> order = new();
    private readonly object gate = new();

    /// <summary>已记录的批次号数量（供回归与诊断检查）。</summary>
    public int Count { get { lock (gate) return states.Count; } }

    /// <summary>生成一个新的批次号。</summary>
    public static Guid NewBatchId() => Guid.NewGuid();

    /// <summary>查一批现在的状态；没记过返回 null。</summary>
    public AgentBatchState? StateOf(Guid batchId)
    {
        lock (gate) return states.TryGetValue(batchId, out var state) ? state : null;
    }

    /// <summary>查一批上次失败在哪一段（返工 S2）；没失败过返回 <see cref="AgentBatchFailure.None"/>。</summary>
    public AgentBatchFailure FailureOf(Guid batchId)
    {
        lock (gate) return failures.TryGetValue(batchId, out var failure) ? failure : AgentBatchFailure.None;
    }

    /// <summary>查一批提交时所在的画布身份（返工 V3）；没记过返回空串。</summary>
    public string CanvasKeyOf(Guid batchId)
    {
        lock (gate) return canvasKeys.TryGetValue(batchId, out var key) ? key : string.Empty;
    }

    /// <summary>
    /// 是否还有**任意**一批在等待恢复（返工 V2）。
    /// 必须能脱离待处理清单判断：已成功保存的批次会把清单清空，此时若撤销只做了一半，
    /// 只看清单就会放行离开，把恢复记录丢在半路。
    /// </summary>
    public bool HasPendingRecovery(out Guid batchId)
    {
        lock (gate)
        {
            foreach (var (id, state) in states)
            {
                if (state != AgentBatchState.NeedsRecovery) continue;
                batchId = id;
                return true;
            }

            batchId = Guid.Empty;
            return false;
        }
    }

    /// <summary>
    /// 开始提交一批。返回 false 表示这一批已经被提交过（成功或失败都算），调用方必须放弃本次提交。
    /// <paramref name="canvasKey"/> 是本次提交所在的画布身份（返工 V3），用于拒绝跨画布提交/续跑。
    /// </summary>
    public bool TryBegin(Guid batchId, string? canvasKey, out string reason)
    {
        reason = string.Empty;
        if (batchId == Guid.Empty)
        {
            reason = "批次标识无效，已拒绝提交。";
            return false;
        }

        var key = canvasKey ?? string.Empty;
        lock (gate)
        {
            // 返工 R17-2：还有**别的**批次在等待恢复时，任何新的提交（含整批回滚后的重新提交）都不许开始——
            // 新批落画会覆盖 lastAgentCommit，旧批的恢复记录随之不可达（死记录），
            // 而账本里旧批仍停在待恢复、所有离开入口又被拦住，用户被锁死。
            // 恢复做完后旧批会变成 RolledBack，这里自然放行。
            if (HasPendingRecovery(out var waiting) && waiting != batchId)
            {
                reason = $"还有一批改动（{waiting.ToString()[..8]}）的撤销没做完：请先重试撤销，把画布与文件恢复好，再提交新的批次。";
                return false;
            }

            if (states.TryGetValue(batchId, out var state))
            {
                // 已整批回滚：画布与副作用都回到提交前，等于没提交过，允许重新来一次。
                if (state == AgentBatchState.RolledBack)
                {
                    states[batchId] = AgentBatchState.Pending;
                    failures.Remove(batchId);
                    canvasKeys[batchId] = key;
                    return true;
                }

                // 返工 V3：同一批次号换到别的画布上提交，是把 A 的动作记成 B 的结果，一律拒绝。
                var recorded = CanvasKeyOf(batchId);
                if (recorded.Length > 0 && key.Length > 0 && !string.Equals(recorded, key, StringComparison.Ordinal))
                {
                    reason = "这一批属于另一个画布，不允许在当前画布上提交或续跑（跨画布提交会把别的画布的动作记到这份画布上）。";
                    return false;
                }

                reason = state switch
                {
                    AgentBatchState.Succeeded => "这一批动作已经提交成功，已拒绝重复提交（同一批只提交一次）。",
                    AgentBatchState.Pending => "这一批正在提交中，已拒绝重复提交。",
                    AgentBatchState.NeedsRecovery => "这一批的撤销只完成了一部分：请先重试撤销把画布与文件恢复好，再重新提交。",
                    _ => "这一批上次提交没有完成：请点「重试保存」续跑，或撤销这批动作后重新提交。"
                };
                return false;
            }

            states[batchId] = AgentBatchState.Pending;
            failures.Remove(batchId);
            canvasKeys[batchId] = key;
            order.Enqueue(batchId);
            while (order.Count > KeepRecent) { states.Remove(order.Dequeue()); }
            return true;
        }
    }

    /// <summary>兼容旧名：等价于 <see cref="TryBegin"/>（不带画布身份）。</summary>
    public bool TryBegin(Guid batchId, out string reason) => TryBegin(batchId, null, out reason);

    /// <summary>兼容旧名：等价于 <see cref="TryBegin"/>。</summary>
    public bool TryConsume(Guid batchId, out string reason) => TryBegin(batchId, out reason);

    /// <summary>
    /// 续跑一批**失败过**的提交（只允许失败状态，且只有「重试保存」这类显式动作会走这里）。
    /// 目的：动作已经应用过、只是没存下来时，不必重复应用动作。
    /// <paramref name="canvasKey"/> 同样参与校验（返工 V3）：换到别的画布上重存同样是错的。
    /// </summary>
    public bool TryResume(Guid batchId, string? canvasKey, out string reason)
    {
        reason = string.Empty;
        var key = canvasKey ?? string.Empty;
        lock (gate)
        {
            if (!states.TryGetValue(batchId, out var state))
            {
                reason = "这一批还没有提交过，不能续跑。";
                return false;
            }

            if (state != AgentBatchState.Failed)
            {
                reason = state switch
                {
                    AgentBatchState.Succeeded => "这一批已经提交成功，无需重试。",
                    AgentBatchState.Pending => "这一批正在提交中。",
                    AgentBatchState.NeedsRecovery => "这一批正在等待恢复：请先重试撤销，不要继续重存（否则会把已回退的画布当成结果存下来）。",
                    _ => "这一批已经回滚，不再需要重存；如需这批改动请重新提交。"
                };
                return false;
            }

            // 返工 V3：动作是落在**某个画布**上的，换画布重存等于把结果写到别的画布上。
            var recorded = CanvasKeyOf(batchId);
            if (recorded.Length > 0 && key.Length > 0 && !string.Equals(recorded, key, StringComparison.Ordinal))
            {
                reason = "这一批的动作是在另一个画布上应用的，不允许在当前画布上续跑保存（否则会把结果写到别的画布上）。";
                return false;
            }

            // 只有「动作已落画、只是没存下来」才允许续跑保存（返工 S2）。
            if (FailureOf(batchId) != AgentBatchFailure.Save)
            {
                reason = "这一批上次的失败发生在动作应用阶段，不能只重存：请修正后重新提交。";
                return false;
            }

            states[batchId] = AgentBatchState.Pending;
            return true;
        }
    }

    /// <summary>兼容旧名：等价于 <see cref="TryResume"/>（不带画布身份）。</summary>
    public bool TryResume(Guid batchId, out string reason) => TryResume(batchId, null, out reason);

    /// <summary>标记一批提交完成（此后重复提交一律拒绝）。</summary>
    public void MarkSucceeded(Guid batchId)
    {
        lock (gate)
        {
            if (!states.ContainsKey(batchId)) return;
            states[batchId] = AgentBatchState.Succeeded;
            failures.Remove(batchId);
        }
    }

    /// <summary>标记一批提交失败，并记下失败发生在哪一段（返工 S2：决定后续是重存还是重来）。</summary>
    public void MarkFailed(Guid batchId, AgentBatchFailure failure)
    {
        lock (gate)
        {
            if (!states.ContainsKey(batchId)) return;
            states[batchId] = AgentBatchState.Failed;
            failures[batchId] = failure == AgentBatchFailure.None ? AgentBatchFailure.Apply : failure;
        }
    }

    /// <summary>标记一批的撤销只完成了一部分：后续只能先把恢复做完（返工 U2）。</summary>
    public void MarkNeedsRecovery(Guid batchId)
    {
        lock (gate)
        {
            if (!states.ContainsKey(batchId)) return;
            states[batchId] = AgentBatchState.NeedsRecovery;
            failures[batchId] = AgentBatchFailure.Rollback;
        }
    }

    /// <summary>标记一批已整批回滚：画布与副作用都回到提交前，允许重新提交同一批。</summary>
    public void MarkRolledBack(Guid batchId)
    {
        lock (gate)
        {
            if (!states.ContainsKey(batchId)) return;
            states[batchId] = AgentBatchState.RolledBack;
            failures.Remove(batchId);
        }
    }

    /// <summary>清空账本。仅用于测试与「重置会话」这类明确场景。</summary>
    public void Reset()
    {
        lock (gate)
        {
            states.Clear();
            failures.Clear();
            canvasKeys.Clear();
            order.Clear();
        }
    }
}
