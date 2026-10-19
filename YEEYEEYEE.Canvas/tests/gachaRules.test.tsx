import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { CandidateSlotView } from '../src/shell/candidateBatch'
import { GachaRevealView, type GachaRevealViewProps } from '../src/shell/GachaReveal'
import {
  EMPTY_GACHA_SESSION,
  GACHA_TIMING,
  canConfirmGacha,
  gachaBatchProgress,
  gachaDeck,
  gachaNextSelection,
  gachaOffset,
  gachaSessionReducer,
  gachaTimeline,
  gachaPreferenceFrom,
  readGachaPreference,
  writeGachaPreference
} from '../src/shell/gachaRules'

/**
 * 开奖的第一阶段：终态判断、牌堆构成、会话状态机与揭晓页的静态输出。
 * 动画本身在浏览器验收里看；这里钉住的是「什么时候能开、谁能进牌堆、旧回包会不会串台」。
 */

const slot = (overrides: Partial<CandidateSlotView> = {}): CandidateSlotView => ({
  key: 'slot-1',
  jobId: 'job-1',
  outputIndex: 0,
  status: 'succeeded',
  label: '已出好',
  progress: null,
  failure: '',
  adoptable: true,
  adopted: false,
  imageUrl: '/api/web/jobs/job-1/outputs/0',
  quality: null,
  ...overrides
})

const failedSlot = (key: string, reason: string): CandidateSlotView =>
  slot({ key, jobId: `job-${key}`, status: 'failed', label: '失败', failure: reason, adoptable: false, imageUrl: null })

const cancelledSlot = (key: string): CandidateSlotView =>
  slot({ key, jobId: `job-${key}`, status: 'cancelled', label: '已取消', adoptable: false, imageUrl: null })

const runningSlot = (key: string): CandidateSlotView =>
  slot({ key, jobId: `job-${key}`, status: 'running', label: '生成中', progress: 40, adoptable: false, imageUrl: null })

describe('整批终态与开奖资格', () => {
  it('还有排队/生成中的槽位时不算终态，也不能开奖', () => {
    const progress = gachaBatchProgress([runningSlot('a'), slot({ key: 'b' })])
    expect(progress.terminal).toBe(false)
    expect(progress.canReveal).toBe(false)
    expect(progress.pending).toBe(1)
  })

  it('部分失败：全部终态后仍可开奖，失败的计数如实保留', () => {
    const progress = gachaBatchProgress([slot({ key: 'a' }), failedSlot('b', '上游 500')])
    expect(progress).toMatchObject({ total: 2, succeeded: 1, failed: 1, pending: 0, terminal: true, canReveal: true, allFailed: false })
  })

  it('全部失败或全部取消：明确是「没有可开奖的图」，不播放成功动画', () => {
    const allFailed = gachaBatchProgress([failedSlot('a', '模型不可用'), failedSlot('b', '超时')])
    expect(allFailed).toMatchObject({ terminal: true, canReveal: false, allFailed: true })
    const allCancelled = gachaBatchProgress([cancelledSlot('a')])
    expect(allCancelled).toMatchObject({ terminal: true, canReveal: false, allFailed: true })
  })

  it('成功但已采用的图不再进牌堆；整批没有待揭晓的图时也不能开', () => {
    const progress = gachaBatchProgress([slot({ key: 'a', adoptable: false, adopted: true }), failedSlot('b', '失败')])
    expect(progress.terminal).toBe(true)
    expect(progress.revealable).toBe(0)
    expect(progress.canReveal).toBe(false)
    expect(progress.allFailed).toBe(false)
  })
})

describe('牌堆：只有成功且未采用的图参与揭晓', () => {
  it('保留候选区序号；失败与取消单列，不伪装成成功卡', () => {
    const deck = gachaDeck([
      slot({ key: 'a', jobId: 'job-a' }),
      failedSlot('b', '上游 500'),
      cancelledSlot('c'),
      slot({ key: 'd', jobId: 'job-d' })
    ])
    expect(deck.cards.map(card => card.number)).toEqual([1, 4])
    expect(deck.cards.map(card => card.jobId)).toEqual(['job-a', 'job-d'])
    expect(deck.failed).toEqual([{ number: 2, label: '失败', reason: '上游 500' }])
    expect(deck.cancelled).toEqual([3])
  })

  it('已采用的图不出现在牌堆里', () => {
    const deck = gachaDeck([slot({ key: 'a', adopted: true, adoptable: false }), slot({ key: 'b' })])
    expect(deck.cards.map(card => card.key)).toEqual(['b'])
  })
})

describe('时序与排布（与 Desktop 一致）', () => {
  it('两张牌的翻面与完成时刻按桌面常数推算', () => {
    const timeline = gachaTimeline(2)
    expect(timeline.flipsAt).toBe(
      GACHA_TIMING.cardsFlyAtMs + GACHA_TIMING.cardFlyStaggerMs + GACHA_TIMING.cardFlyMs + GACHA_TIMING.settleHoldMs)
    expect(timeline.allDoneAt).toBe(
      timeline.flipsAt + GACHA_TIMING.flipStaggerMs + GACHA_TIMING.flipHalfMs + GACHA_TIMING.flipBackMs + GACHA_TIMING.allDonePadMs)
  })

  it('4 张走 2×2：飞卡起点左右对称，末行不按满行算偏移', () => {
    expect(gachaOffset(4, 0).x).toBeLessThan(0)
    expect(gachaOffset(4, 1).x).toBeGreaterThan(0)
    expect(gachaOffset(4, 0).x).toBe(-gachaOffset(4, 1).x)
    expect(gachaOffset(4, 3).y).toBeGreaterThan(gachaOffset(4, 0).y)
  })

  it('键盘选卡到头就停住', () => {
    expect(gachaNextSelection(null, 1, 3)).toBe(0)
    expect(gachaNextSelection(null, -1, 3)).toBe(2)
    expect(gachaNextSelection(2, 1, 3)).toBe(2)
    expect(gachaNextSelection(0, -1, 3)).toBe(0)
  })
})

describe('提交闸门：重复点击与未加载的图不能被采用', () => {
  const cards = gachaDeck([slot({ key: 'a' }), slot({ key: 'b' })]).cards

  it('没选中 / 提交中 / 图片加载失败都不能按「用这一张」', () => {
    expect(canConfirmGacha({ selection: null, cards, loadFailed: new Set(), adopting: false })).toBe(false)
    expect(canConfirmGacha({ selection: 0, cards, loadFailed: new Set(), adopting: true })).toBe(false)
    expect(canConfirmGacha({ selection: 0, cards, loadFailed: new Set(['a']), adopting: false })).toBe(false)
    expect(canConfirmGacha({ selection: 0, cards, loadFailed: new Set(['b']), adopting: false })).toBe(true)
  })
})

describe('浏览器偏好', () => {
  it('只认明确打开的值；存储不可用时退回关闭', () => {
    expect(gachaPreferenceFrom('on')).toBe(true)
    expect(gachaPreferenceFrom('true')).toBe(true)
    expect(gachaPreferenceFrom('off')).toBe(false)
    expect(readGachaPreference(null)).toBe(false)
    const store = new Map<string, string>()
    writeGachaPreference(true, { getItem: key => store.get(key) ?? null, setItem: (key, value) => { store.set(key, value) } })
    expect(readGachaPreference({ getItem: key => store.get(key) ?? null, setItem: () => {} })).toBe(true)
    writeGachaPreference(false, { getItem: key => store.get(key) ?? null, setItem: (key, value) => { store.set(key, value) } })
    expect(readGachaPreference({ getItem: key => store.get(key) ?? null, setItem: () => {} })).toBe(false)
  })
})

describe('揭晓会话：关闭、重开、采用成败、换批与旧回包', () => {
  const slots = [slot({ key: 'a' }), failedSlot('b', '上游 500')]
  const opened = gachaSessionReducer(EMPTY_GACHA_SESSION, {
    type: 'open', batchId: 'batch-1', nodeId: 'node-1', nodeTitle: '镜头一', slots, run: 1
  })

  it('关闭只收弹层；再次打开还是同一批、可以选择', () => {
    const closed = gachaSessionReducer(opened, { type: 'close' })
    expect(closed.open).toBe(false)
    expect(closed.slots).toHaveLength(0)
    const reopened = gachaSessionReducer(closed, {
      type: 'open', batchId: 'batch-1', nodeId: 'node-1', nodeTitle: '镜头一', slots, run: closed.run + 1
    })
    expect(reopened.open).toBe(true)
    expect(reopened.batchId).toBe('batch-1')
    expect(reopened.slots).toBe(slots)
    expect(reopened.selection).toBeNull()
  })

  it('判档说明随打开快照固定：之后结果到达也不会改写这一层', () => {
    const noted = gachaSessionReducer(EMPTY_GACHA_SESSION, {
      type: 'open', batchId: 'batch-1', nodeId: 'node-1', nodeTitle: '镜头一', slots, run: 1,
      qualityNote: '判档尚未结束：现在开奖先按未判档展示。'
    })
    expect(noted.qualityNote).toBe('判档尚未结束：现在开奖先按未判档展示。')
    // 后续任何批次状态变化都不经过这个 reducer；关闭后再开才会按新状态重新取。
    const closed = gachaSessionReducer(noted, { type: 'close' })
    expect(closed.qualityNote).toBe('')
  })

  it('提交期间重复点同一张不会开始第二次采用', () => {
    const selected = gachaSessionReducer(opened, { type: 'select', index: 0 })
    const first = gachaSessionReducer(selected, { type: 'beginAdopt', key: 'a', run: 2 })
    expect(first.adopting).toBe(true)
    const second = gachaSessionReducer(first, { type: 'beginAdopt', key: 'a', run: 3 })
    expect(second).toBe(first)
    expect(second.run).toBe(2)
  })

  it('采用失败保留候选与选中，并显示服务端原话；可以再试', () => {
    const selected = gachaSessionReducer(opened, { type: 'select', index: 0 })
    const submitting = gachaSessionReducer(selected, { type: 'beginAdopt', key: 'a', run: 2 })
    const failed = gachaSessionReducer(submitting, { type: 'adoptFailed', run: 2, message: '采用失败：目标节点被锁定' })
    expect(failed).toMatchObject({ open: true, selection: 0, adopting: false, error: '采用失败：目标节点被锁定', submitting: '' })
    const retry = gachaSessionReducer(failed, { type: 'beginAdopt', key: 'a', run: 3 })
    expect(retry.adopting).toBe(true)
  })

  it('采用成功收起整批（会话关闭），旧的失败回包不影响', () => {
    const submitting = gachaSessionReducer(opened, { type: 'beginAdopt', key: 'a', run: 2 })
    const done = gachaSessionReducer(submitting, { type: 'adoptSucceeded', run: 2 })
    expect(done.open).toBe(false)
    const late = gachaSessionReducer(done, { type: 'adoptFailed', run: 2, message: '迟到的失败' })
    expect(late).toBe(done)
  })

  it('批次被替换/项目切换：会话作废，旧批次的回包一律丢弃', () => {
    const submitting = gachaSessionReducer(opened, { type: 'beginAdopt', key: 'a', run: 2 })
    const replaced = gachaSessionReducer(submitting, { type: 'invalidate' })
    expect(replaced.open).toBe(false)
    expect(replaced.run).toBe(submitting.run + 1)
    expect(gachaSessionReducer(replaced, { type: 'adoptSucceeded', run: 2 })).toBe(replaced)
    expect(gachaSessionReducer(replaced, { type: 'adoptFailed', run: 2, message: '旧回包' })).toBe(replaced)
  })
})

describe('揭晓页的静态输出', () => {
  const slots = [slot({ key: 'a', jobId: 'job-a' }), failedSlot('b', '上游 500')]
  const progress = gachaBatchProgress(slots)
  const deck = gachaDeck(slots)
  const view = (overrides: Partial<GachaRevealViewProps> = {}) => renderToStaticMarkup(createElement(GachaRevealView, {
    nodeTitle: '镜头一',
    progress,
    deck,
    phase: 'ready',
    loadFailed: new Set<string>(),
    tints: {},
    loadedCount: deck.cards.length,
    selection: null,
    adopting: false,
    error: '',
    onSelect: () => {},
    onAdopt: () => {},
    onClose: () => {},
    onSkip: () => {},
    onRetry: () => {},
    ...overrides
  }))

  it('可挑选阶段：卡面翻正、显示「未判档」，失败项如实列出', () => {
    const markup = view({ selection: 0 })
    expect(markup).toContain('data-phase="ready"')
    expect(markup).toContain('is-up')
    expect(markup).toContain('未判档')
    expect(markup).toContain('已选中第 1 张')
    expect(markup).toContain('第 2 张 · 失败：上游 500')
    expect(markup).toContain('第 1 张候选图')
    expect(markup).not.toContain('跳过动画')
    expect(markup).not.toContain('disabled=""')
  })

  it('动画阶段：提供跳过/先不开，未选中时「用这一张」不可按', () => {
    const markup = view({ phase: 'wish' })
    expect(markup).toContain('跳过动画')
    expect(markup).toContain('先不开')
    expect(markup).toContain('disabled=""')
  })

  it('预加载中不白屏；全部加载失败时给出重试与关闭，不播放成功动画', () => {
    expect(view({ phase: 'loading', loadedCount: 1 })).toContain('正在预加载这批图（1/1）')
    const broken = view({ phase: 'loadError' })
    expect(broken).toContain('这批图没有加载出来')
    expect(broken).toContain('重新加载')
    expect(broken).toContain('先不开')
    expect(broken).not.toContain('跳过动画')
  })

  it('采用失败时错误显示在脚下，选中与「用这一张」都还在', () => {
    const markup = view({ selection: 0, error: '采用失败：目标节点被锁定' })
    expect(markup).toContain('采用失败：目标节点被锁定')
    expect(markup).toContain('已选中第 1 张')
    expect(markup).not.toContain('disabled=""')
  })
})

describe('判档接入开奖：档位、裂纹、跳过与关闭开关', () => {
  const gradedSlot = (quality: CandidateSlotView['quality']) => [slot({ key: 'a', jobId: 'job-a', quality })]
  const gradedView = (quality: CandidateSlotView['quality'], overrides: Partial<GachaRevealViewProps> = {}) => {
    const slots = gradedSlot(quality)
    return renderToStaticMarkup(createElement(GachaRevealView, {
      nodeTitle: '镜头一',
      progress: gachaBatchProgress(slots),
      deck: gachaDeck(slots),
      phase: 'ready',
      loadFailed: new Set<string>(),
      tints: {},
      loadedCount: 1,
      selection: 0,
      adopting: false,
      error: '',
      onSelect: () => {},
      onAdopt: () => {},
      onClose: () => {},
      onSkip: () => {},
      onRetry: () => {},
      ...overrides
    }))
  }

  it('判过档的卡片显示档位与分数，并带上「不是客观结论」的说明', () => {
    const markup = gradedView({
      outputIndex: 0, status: 'graded', source: 'model', tier: 'gold', score: 8, reason: '主体清楚'
    })
    expect(markup).toContain('金 8/10')
    expect(markup).toContain('第 1 张 · 金档 8/10（模型判定）：主体清楚')
    expect(markup).toContain('不是客观结论')
    expect(markup).not.toContain('未判档')
  })

  it('裂纹卡裂开并把命中的负面词写在档位牌下面', () => {
    const markup = gradedView({
      outputIndex: 0, status: 'graded', source: 'model', tier: 'red', score: 9,
      negativeHits: ['多余手指'], reason: '左手多一根手指'
    })
    expect(markup).toContain('裂纹 9/10')
    expect(markup).toContain('命中：多余手指')
    expect(markup).toContain('is-cracked')
    expect(markup).toContain('裂纹，踩中负面词：多余手指')
  })

  it('跳过与失败明确显示，不当成未判档或最低档；跳过之后仍可开奖与采用', () => {
    const skipped = gradedView({ outputIndex: 0, status: 'skipped' })
    expect(skipped).toContain('判档已跳过')
    expect(skipped).not.toContain('disabled=""')
    expect(gradedView({ outputIndex: 0, status: 'failed', reason: '模型超时' })).toContain('判档失败')
  })

  it('判档开关关着时即使服务端有结果也只写「未判档」', () => {
    const markup = gradedView(
      { outputIndex: 0, status: 'graded', source: 'model', tier: 'gold', score: 8 },
      { showQuality: false })
    expect(markup).toContain('未判档')
    expect(markup).not.toContain('金 8/10')
  })

  it('判档尚未结束时说明可以跳过直接开奖，且不影响选中与采用', () => {
    const markup = gradedView(null, { qualityNote: '判档尚未结束：现在开奖先按未判档展示。' })
    expect(markup).toContain('判档尚未结束')
    expect(markup).toContain('已选中第 1 张')
    expect(markup).not.toContain('disabled=""')
  })
})
