import { describe, expect, it } from 'vitest'
import {
  DEFAULT_MAX_ATTEMPTS,
  canRetryJob,
  describeJobAttempt,
  describeJobStatus,
  describeRetryLineage,
  isRetryableJobState,
  isTerminalJobState,
  jobAttempt,
  jobStateLabel
} from '../src/JobView'
import type { JobUpdate } from '../src/Protocol/VersionedMessages'

function update(patch: Partial<JobUpdate>): JobUpdate {
  return { jobId: 'a1b2c3d4-0000-0000-0000-000000000000', invocationId: 'inv', state: 'Running', progressPercent: 25, outputs: [], ...patch }
}

describe('任务状态与重试语义跨端一致（目标 5）', () => {
  it('终态与可重试状态与桌面口径一致', () => {
    expect(isTerminalJobState('Succeeded')).toBe(true)
    expect(isTerminalJobState('Failed')).toBe(true)
    expect(isTerminalJobState('Running')).toBe(false)
    expect(isRetryableJobState('Failed')).toBe(true)
    expect(isRetryableJobState('Cancelled')).toBe(true)
    expect(isRetryableJobState('Succeeded')).toBe(false)
    expect(isRetryableJobState('Queued')).toBe(false)
  })

  it('尝试次数缺失或非法时按第 1 次处理', () => {
    expect(jobAttempt(update({}))).toBe(1)
    expect(jobAttempt(update({ attempt: 0 }))).toBe(1)
    expect(jobAttempt(update({ attempt: -3 }))).toBe(1)
    expect(jobAttempt(update({ attempt: 2.5 }))).toBe(1)
    expect(jobAttempt(update({ attempt: 3 }))).toBe(3)
  })

  it('重试可用性受状态与尝试上限共同约束', () => {
    expect(canRetryJob(update({ state: 'Failed', attempt: 1 }))).toBe(true)
    expect(canRetryJob(update({ state: 'Failed', attempt: DEFAULT_MAX_ATTEMPTS }))).toBe(false)
    expect(canRetryJob(update({ state: 'Succeeded', attempt: 1 }))).toBe(false)
    expect(canRetryJob(update({ state: 'Failed', attempt: 2 }), 2)).toBe(false)
  })

  it('尝试与血缘描述可读，且首次尝试不伪造血缘', () => {
    expect(describeJobAttempt(update({ attempt: 2 }))).toBe('第 2 次尝试（共最多 3 次）')
    expect(describeRetryLineage(update({}))).toBe('首次尝试')
    expect(describeRetryLineage(update({ retryOfJobId: 'a1b2c3d4-1111-2222-3333-444455556666' }))).toBe('重试自 a1b2c3d4')
  })

  it('状态行只在与重试有关时附带尝试信息', () => {
    expect(describeJobStatus(update({ state: 'Running', progressPercent: 40 }))).toBe('进行中 · 40%')
    expect(describeJobStatus(update({ state: 'Failed', progressPercent: 0, attempt: 2, retryOfJobId: 'deadbeef-0000', errorMessage: '写盘失败' })))
      .toBe('失败 · 0% · 第 2 次尝试（共最多 3 次） · 重试自 deadbeef · 写盘失败')
  })

  it('状态标签覆盖全部JobState', () => {
    expect(jobStateLabel('Queued')).toBe('排队中')
    expect(jobStateLabel('Cancelling')).toBe('取消中')
    expect(jobStateLabel('Cancelled')).toBe('已取消')
    expect(jobStateLabel('Succeeded')).toBe('已完成')
  })
})
