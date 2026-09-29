import type { JobState, JobUpdate } from './Protocol/VersionedMessages'

/**
 * 任务状态与重试语义（目标 5 / 桌面与 Web 状态一致）。
 *
 * 与桌面端 `JobRetryPolicy` 保持同一口径：可重试的只有失败与已取消两个终态，
 * 同一次输入的反复尝试有次数上限；浏览器与桌面看到的「第几次尝试、重试自哪个任务」必须一致。
 */

/** 与桌面端 `JobRetryPolicy.DefaultMaxAttempts` 一致：首次 + 2 次重试。 */
export const DEFAULT_MAX_ATTEMPTS = 3

export function isTerminalJobState(state: JobState): boolean {
  return state === 'Succeeded' || state === 'Failed' || state === 'Cancelled'
}

/** 可重试的状态：失败与已取消（含外部任务取消）。 */
export function isRetryableJobState(state: JobState): boolean {
  return state === 'Failed' || state === 'Cancelled'
}

/** 尝试次数：缺失或非法一律按第 1 次，绝不显示成 0 或负数。 */
export function jobAttempt(update: JobUpdate): number {
  const value = update.attempt
  return typeof value === 'number' && Number.isInteger(value) && value >= 1 ? value : 1
}

export function normalizeMaxAttempts(maxAttempts: number): number {
  return !Number.isFinite(maxAttempts) || maxAttempts < 1 ? 1 : Math.floor(maxAttempts)
}

/** 该任务现在能否重试（与桌面按钮的可用性判定一致）。 */
export function canRetryJob(update: JobUpdate, maxAttempts: number = DEFAULT_MAX_ATTEMPTS): boolean {
  return isRetryableJobState(update.state) && jobAttempt(update) < normalizeMaxAttempts(maxAttempts)
}

/** 「第 2 次尝试（共最多 3 次）」。 */
export function describeJobAttempt(update: JobUpdate, maxAttempts: number = DEFAULT_MAX_ATTEMPTS): string {
  return `第 ${jobAttempt(update)} 次尝试（共最多 ${normalizeMaxAttempts(maxAttempts)} 次）`
}

/** 血缘描述：「重试自 a1b2c3d4」；首次尝试显示「首次尝试」。 */
export function describeRetryLineage(update: JobUpdate): string {
  const retryOf = update.retryOfJobId
  if (typeof retryOf !== 'string' || retryOf.length === 0) return '首次尝试'
  return `重试自 ${shortId(retryOf)}`
}

/** 稳定的中文状态标签，两端一致。 */
export function jobStateLabel(state: JobState): string {
  switch (state) {
    case 'Queued': return '排队中'
    case 'Running': return '进行中'
    case 'Cancelling': return '取消中'
    case 'Succeeded': return '已完成'
    case 'Failed': return '失败'
    case 'Cancelled': return '已取消'
    default: return String(state)
  }
}

/** 状态行：例如「进行中 · 第 2 次尝试（共最多 3 次） · 重试自 a1b2c3d4」。 */
export function describeJobStatus(update: JobUpdate, maxAttempts: number = DEFAULT_MAX_ATTEMPTS): string {
  const attempt = jobAttempt(update) > 1 || typeof update.retryOfJobId === 'string'
    ? ` · ${describeJobAttempt(update, maxAttempts)} · ${describeRetryLineage(update)}`
    : ''
  const failure = update.state === 'Failed' && typeof update.errorMessage === 'string' && update.errorMessage.length > 0
    ? ` · ${update.errorMessage}`
    : ''
  return `${jobStateLabel(update.state)} · ${update.progressPercent}%${attempt}${failure}`
}

function shortId(value: string): string {
  return value.length > 8 ? value.slice(0, 8) : value
}
