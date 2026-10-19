import { describe, expect, it } from 'vitest'
import {
  CRACK_COLOR,
  QUALITY_PREF_KEY,
  UNGRADED_SPARKS,
  parseQualities,
  qualityBadge,
  qualityOmen,
  qualityPreferenceFrom,
  qualitySelectionLine,
  qualitySparks,
  qualityStartDecision,
  readQualityPreference,
  writeQualityPreference,
  type JobQuality
} from '../src/shell/imageQuality'

/**
 * 判档的第二阶段：开关默认关、卡面不把未判档画成白档、跳过/失败/最低档互相区分，
 * 以及「关着不发请求 / 已经开始过不重复发 / 没模型只报配置」这三条入口规则。
 */

const graded = (overrides: Partial<JobQuality> = {}): JobQuality => ({
  outputIndex: 0,
  status: 'graded',
  source: 'model',
  tier: 'gold',
  score: 8,
  reason: '主体清楚，手指正常',
  negativeHits: [],
  ruleVersion: 'desktop-quality-v1',
  ...overrides
})

describe('判档偏好：默认关', () => {
  it('只认明确打开的值；存储不可用时退回关闭', () => {
    expect(qualityPreferenceFrom('on')).toBe(true)
    expect(qualityPreferenceFrom('true')).toBe(true)
    expect(qualityPreferenceFrom('1')).toBe(true)
    expect(qualityPreferenceFrom('off')).toBe(false)
    expect(qualityPreferenceFrom(null)).toBe(false)
    expect(readQualityPreference(null)).toBe(false)
    const store = new Map<string, string>()
    writeQualityPreference(true, { getItem: key => store.get(key) ?? null, setItem: (key, value) => { store.set(key, value) } })
    expect(store.get(QUALITY_PREF_KEY)).toBe('on')
    expect(readQualityPreference({ getItem: key => store.get(key) ?? null, setItem: () => {} })).toBe(true)
    writeQualityPreference(false, { getItem: key => store.get(key) ?? null, setItem: (key, value) => { store.set(key, value) } })
    expect(readQualityPreference({ getItem: key => store.get(key) ?? null, setItem: () => {} })).toBe(false)
  })
})

describe('卡面：未判档、跳过、失败、最低档互不冒充', () => {
  it('没有结果写「未判档」，不是白档', () => {
    const badge = qualityBadge(null)
    expect(badge.text).toBe('未判档')
    expect(badge.graded).toBe(false)
    expect(qualitySparks(null)).toBe(UNGRADED_SPARKS)
    expect(qualityOmen([null])).toBeNull()
  })

  it('跳过与失败各有自己的词，且不算判过档', () => {
    expect(qualityBadge({ outputIndex: 0, status: 'skipped' }).text).toBe('判档已跳过')
    expect(qualityBadge({ outputIndex: 0, status: 'failed' }).text).toBe('判档失败')
    expect(qualitySparks({ outputIndex: 0, status: 'failed' })).toBe(UNGRADED_SPARKS)
    expect(qualityOmen([{ outputIndex: 0, status: 'failed' }, null])).toBeNull()
  })

  it('判出来的白档仍是白档：有档位、光点比未判档少', () => {
    const white = graded({ tier: 'white', score: 2 })
    expect(qualityBadge(white)).toMatchObject({ text: '白 2/10', graded: true, cracked: false })
    expect(qualitySparks(white)).toBe(3)
    expect(qualitySparks(null)).toBe(4)
  })

  it('踩中负面词的卡写「裂纹」并用裂纹色，分数仍保留', () => {
    const cracked = graded({ tier: 'red', score: 9, negativeHits: ['多余手指', '水印'] })
    const badge = qualityBadge(cracked)
    expect(badge.text).toBe('裂纹 9/10')
    expect(badge.cracked).toBe(true)
    expect(badge.color).toBe(CRACK_COLOR)
    expect(qualitySelectionLine(2, 4, cracked)).toBe(
      '第 2 张 · 红档 9/10（模型判定） · 裂纹，踩中负面词：多余手指、水印：主体清楚，手指正常')
  })

  it('选中说明写清谁判的、分数与理由；未判过不编理由', () => {
    expect(qualitySelectionLine(1, 2, graded())).toBe('第 1 张 · 金档 8/10（模型判定）：主体清楚，手指正常')
    expect(qualitySelectionLine(1, 2, null)).toBe('已选中第 1 张（共 2 张）· 未判档')
    expect(qualitySelectionLine(1, 2, { outputIndex: 0, status: 'skipped' })).toContain('判档已跳过')
    expect(qualitySelectionLine(1, 2, { outputIndex: 0, status: 'failed' })).toContain('判档失败')
  })

  it('预兆取最高档但排除裂纹卡；全批只剩裂纹卡时按白档', () => {
    expect(qualityOmen([graded({ tier: 'blue' }), graded({ tier: 'red' })])).toBe('red')
    expect(qualityOmen([graded({ tier: 'red', negativeHits: ['水印'] }), graded({ tier: 'purple' })])).toBe('purple')
    expect(qualityOmen([graded({ tier: 'red', negativeHits: ['水印'] })])).toBe('white')
  })
})

describe('何时发起判档：关着不发、开始过不重复发、没模型只报配置', () => {
  const base = {
    terminal: true,
    succeeded: 2,
    revealable: 2,
    started: false,
    qualities: [null, null] as Array<JobQuality | null>,
    availability: { available: true }
  }

  it('开关关闭时直接不发起', () => {
    expect(qualityStartDecision({ ...base, enabled: false })).toBe('off')
  })

  it('整批未结束 / 已开始过 / 已全部判过都不再发起', () => {
    expect(qualityStartDecision({ ...base, enabled: true, terminal: false })).toBe('pending')
    expect(qualityStartDecision({ ...base, enabled: true, started: true })).toBe('pending')
    expect(qualityStartDecision({
      ...base, enabled: true, qualities: [graded(), graded({ outputIndex: 0 })]
    })).toBe('graded')
  })

  it('能力未读到先等；没有可读图模型时只报不可用，不发起请求', () => {
    expect(qualityStartDecision({ ...base, enabled: true, availability: null })).toBe('wait')
    expect(qualityStartDecision({ ...base, enabled: true, availability: { available: false } })).toBe('unavailable')
    expect(qualityStartDecision({ ...base, enabled: true })).toBe('start')
  })
})

describe('读取服务端结果', () => {
  it('按 outputIndex 保留，坏字段丢弃；不改写档位', () => {
    const parsed = parseQualities([
      {
        jobId: 'job-1', outputIndex: 0, status: 'graded', source: 'model', tier: 'red', score: 9,
        reason: '好', negativeHits: ['水印'], ruleVersion: 'desktop-quality-v1'
      },
      { jobId: 'job-1', outputIndex: '0', status: 'graded', tier: 'gold' },
      { jobId: 'job-1', outputIndex: 1, status: 'graded', tier: 'orange', score: 7 },
      { jobId: 'job-1', outputIndex: 2, status: 'unknown' }
    ])
    expect(parsed).toHaveLength(2)
    expect(parsed[0]).toMatchObject({ outputIndex: 0, tier: 'red', score: 9, negativeHits: ['水印'] })
    expect(parsed[1]).toMatchObject({ outputIndex: 1, tier: null, score: 7 })
    expect(parsed[1].status).toBe('graded')
  })
})
