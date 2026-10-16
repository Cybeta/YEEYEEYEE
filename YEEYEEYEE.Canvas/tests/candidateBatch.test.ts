import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import {
  CANDIDATE_CARD,
  candidateBatchViews,
  candidateCardRects,
  candidateHeadline,
  type CandidateBatch
} from '../src/shell/candidateBatch'
import { CanvasView } from '../src/shell/CanvasView'
import { jobOutputAttachmentId, type NodeJob } from '../src/shell/nodeMedia'
import type { ViewRecord } from '../src/shell/records'
import type { AttachmentSummary } from '../src/assets'

/**
 * 节点出图的候选批次：批次 / 槽位 / 任务 / 原节点怎么对应，卡片什么时候出现、什么时候说「待确认」。
 *
 * 这些都是纯计算与渲染，SSR 就能钉住（不跑 effect、不发请求）。
 */

const NODE = '44444444-4444-4444-8444-444444444444'
const OTHER = '55555555-5555-4555-8555-555555555555'
const JOB_A = '3f2504e0-4f89-11d3-9a0c-0305e82c3301'
const JOB_B = 'ffffffff-ffff-ffff-ffff-ffffffffffff'

const record = (recordId: string, title: string, attachments: AttachmentSummary[] = []): ViewRecord => ({
  recordId, recordType: 'storyboard', record: { title, content: '', x: 0, y: 0, attachments }
})
const job = (overrides: Partial<NodeJob> & { jobId: string; state: string }): NodeJob => overrides
const batch = (jobIds: Array<string | null>, overrides: Partial<CandidateBatch> = {}): CandidateBatch => ({
  batchId: 'batch-1',
  nodeId: NODE,
  startedAt: 1_000,
  slots: jobIds.map((jobId, index) => ({ key: `slot-${index}`, jobId, submitError: '' })),
  ...overrides
})

describe('候选批次：槽位与任务、原节点的对应', () => {
  it('提交几张就是几个槽位；卡片顺序跟提交顺序走', () => {
    const views = candidateBatchViews([batch([JOB_A, JOB_B])], [
      job({ jobId: JOB_A, state: 'Running', targetNodeId: NODE, progressPercent: 20 }),
      job({ jobId: JOB_B, state: 'Queued', targetNodeId: NODE })
    ], [record(NODE, '镜头一')])
    expect(views).toHaveLength(1)
    expect(views[0].slots.map(slot => slot.jobId)).toEqual([JOB_A, JOB_B])
    expect(views[0].slots.map(slot => slot.outputIndex)).toEqual([0, 0])
  })

  it('槽位状态跟着任务走：排队 / 生成中 / 已出好 / 失败 / 已取消', () => {
    const views = candidateBatchViews([batch([JOB_A, JOB_B])], [
      job({ jobId: JOB_A, state: 'Running', targetNodeId: NODE, progressPercent: 37.4 }),
      job({ jobId: JOB_B, state: 'Failed', targetNodeId: NODE, errorMessage: '上游 500' })
    ], [record(NODE, '镜头一')])
    expect(views[0].slots[0]).toMatchObject({ status: 'running', label: '生成中', progress: 37.4 })
    expect(views[0].slots[1]).toMatchObject({ status: 'failed', label: '失败', failure: '上游 500' })
    expect(views[0].headline).toBe('已出好 0/2 · 失败 1')

    const cancelled = candidateBatchViews([batch([JOB_A])], [
      job({ jobId: JOB_A, state: 'Cancelled', targetNodeId: NODE })
    ], [record(NODE, '镜头一')])
    // 取消不是失败：状态如实说已取消，不该报成错误。
    expect(cancelled[0].slots[0]).toMatchObject({ status: 'cancelled', label: '已取消', failure: '' })
  })

  it('任务还没进列表算「排队中」；提交就失败的那一格当场说失败（不留永远等待的卡片）', () => {
    const pending = candidateBatchViews([batch([null, JOB_B])], [
      job({ jobId: JOB_B, state: 'Queued', targetNodeId: NODE })
    ], [record(NODE, '镜头一')])
    expect(pending[0].slots[0]).toMatchObject({ status: 'waiting', label: '排队中', failure: '' })

    const failed: CandidateBatch = batch([null], {
      slots: [{ key: 'slot-0', jobId: null, submitError: '[IMAGE_RATIO_REQUIRED] 请选择图片比例' }]
    })
    const views = candidateBatchViews([failed], [], [record(NODE, '镜头一')])
    expect(views[0].slots[0]).toMatchObject({
      status: 'failed', label: '提交失败', failure: '[IMAGE_RATIO_REQUIRED] 请选择图片比例'
    })
  })

  it('进度：有可靠百分比才显示；缺值 / 越界都不编（越界的夹进 0～100）', () => {
    const views = candidateBatchViews([batch([JOB_A, JOB_B])], [
      job({ jobId: JOB_A, state: 'Running', targetNodeId: NODE }),
      job({ jobId: JOB_B, state: 'Running', targetNodeId: NODE, progressPercent: 180 })
    ], [record(NODE, '镜头一')])
    expect(views[0].slots[0].progress).toBeNull()
    expect(views[0].slots[1].progress).toBe(100)
  })
})

describe('候选批次：成功与采用', () => {
  it('成功后给的是任务产物那条地址（还没采用，节点上没有这件附件）', () => {
    const views = candidateBatchViews([batch([JOB_A])], [
      job({ jobId: JOB_A, state: 'Succeeded', targetNodeId: NODE, attachmentState: 'none' })
    ], [record(NODE, '镜头一')])
    expect(views[0].slots[0]).toMatchObject({
      status: 'succeeded', label: '已出好', adoptable: true, adopted: false,
      imageUrl: `/api/web/jobs/${JOB_A}/outputs/0`
    })
    expect(views[0].slots[0].failure).toBe('')
  })

  it('节点附件里出现这件产物（服务端算出来的那个 ID）才算采用过', () => {
    const adopted = [{
      id: jobOutputAttachmentId(JOB_A, 0).toUpperCase(), kind: 'Image', name: '图片 · 3f2504e0 · 1', source: 'comfyui.text-to-image'
    }]
    const views = candidateBatchViews([batch([JOB_A])], [
      job({ jobId: JOB_A, state: 'Succeeded', targetNodeId: NODE })
    ], [record(NODE, '镜头一', adopted)])
    expect(views[0].slots[0]).toMatchObject({ adopted: true, adoptable: false })
  })

  it('别的产物的附件不会被当成这件候选（按 ID 认，不按名字认）', () => {
    const views = candidateBatchViews([batch([JOB_A])], [
      job({ jobId: JOB_A, state: 'Succeeded', targetNodeId: NODE })
    ], [record(NODE, '镜头一', [{ id: '别人的图', kind: 'Image', name: '同名', source: '' }])])
    expect(views[0].slots[0]).toMatchObject({ adopted: false, adoptable: true })
  })

  it('部分成功部分失败：成功的照样可采用，失败的各自带原因', () => {
    const views = candidateBatchViews([batch([JOB_A, JOB_B])], [
      job({ jobId: JOB_A, state: 'Succeeded', targetNodeId: NODE }),
      job({ jobId: JOB_B, state: 'Failed', targetNodeId: NODE, errorMessage: '模型不可用' })
    ], [record(NODE, '镜头一')])
    expect(views[0].slots[0].adoptable).toBe(true)
    expect(views[0].slots[1].failure).toBe('模型不可用')
    expect(views[0].headline).toBe('已出好 1/2 · 失败 1')
  })
})

describe('候选卡的布局：跟着原节点走', () => {
  it('整排居中挂在节点上方，序号从左到右', () => {
    const rects = candidateCardRects(3, { x: 1000, y: 800 })
    const total = 3 * CANDIDATE_CARD.width + 2 * CANDIDATE_CARD.gap
    expect(rects[0].left).toBe(1000 + (232 - total) / 2)
    expect(rects[1].left - rects[0].left).toBe(CANDIDATE_CARD.width + CANDIDATE_CARD.gap)
    expect(rects[0].top).toBe(800 - CANDIDATE_CARD.height - CANDIDATE_CARD.lift)
  })

  it('顶到世界边界就翻到节点下方', () => {
    const rects = candidateCardRects(1, { x: 100, y: 40 })
    expect(rects[0].top).toBe(40 + 104 + CANDIDATE_CARD.lift)
  })

  it('节点一移动，卡片位置跟着变（位置由节点坐标现算，不另存偏移）', () => {
    const before = candidateCardRects(2, { x: 300, y: 400 })
    const moved = candidateCardRects(2, { x: 700, y: 900 })
    expect(moved[0].left - before[0].left).toBe(400)
    expect(moved[0].top - before[0].top).toBe(500)
    expect(moved[1].left - moved[0].left).toBe(CANDIDATE_CARD.width + CANDIDATE_CARD.gap)
  })
})

describe('批次摘要', () => {
  it('按真实计数说话，不编完成度', () => {
    expect(candidateHeadline([
      { key: 'a', jobId: 'j', outputIndex: 0, status: 'succeeded', label: '已出好', progress: null, failure: '', adoptable: false, adopted: true, imageUrl: null },
      { key: 'b', jobId: 'j', outputIndex: 0, status: 'running', label: '生成中', progress: 10, failure: '', adoptable: false, adopted: false, imageUrl: null }
    ])).toBe('已出好 1/2')
    expect(candidateHeadline([
      { key: 'a', jobId: 'j', outputIndex: 0, status: 'cancelled', label: '已取消', progress: null, failure: '', adoptable: false, adopted: false, imageUrl: null }
    ])).toBe('已出好 0/1 · 已取消 1')
  })
})

/**
 * 画布上的实际输出：候选卡是**另一层**，不进正式节点，也不改节点自己的数据。
 */
describe('画布：候选卡与节点状态', () => {
  const canvas = (options: {
    records?: ViewRecord[]
    jobs?: NodeJob[]
    batches?: CandidateBatch[]
    selectedId?: string
  } = {}) => renderToStaticMarkup(createElement(CanvasView, {
    records: options.records ?? [record(NODE, '镜头一')],
    selectedId: options.selectedId ?? '',
    activeChapter: 'all',
    assets: [], assetsReady: true, showReferencePreviews: true,
    zoom: 1, pan: { x: 0, y: 0 }, onZoom: () => {}, onPan: () => {}, onSelect: () => {},
    jobs: options.jobs,
    candidates: options.batches
      ? candidateBatchViews(options.batches, options.jobs, options.records ?? [record(NODE, '镜头一')])
      : undefined
  }))

  it('提交两张就摆两张卡：一张在等、一张已出好（露出图片与「用这一张」）', () => {
    const jobs = [
      job({ jobId: JOB_A, state: 'Running', targetNodeId: NODE, progressPercent: 15 }),
      job({ jobId: JOB_B, state: 'Succeeded', targetNodeId: NODE, attachmentState: 'none' })
    ]
    const markup = canvas({ jobs, batches: [batch([JOB_A, JOB_B])] })
    expect(markup.match(/class="df-candidate-card/g) ?? []).toHaveLength(2)
    expect(markup).toContain('生成中')
    expect(markup).toContain('15%')
    expect(markup).toContain(`/api/web/jobs/${JOB_B}/outputs/0`)
    expect(markup).toContain('用这一张')
    expect(markup).toContain('已出好 1/2')
    expect(markup).toContain('丢弃这一批')
  })

  it('有候选等采用时节点显示「待确认」，不是「已完成」', () => {
    const jobs = [job({ jobId: JOB_A, state: 'Succeeded', targetNodeId: NODE, attachmentState: 'none' })]
    const markup = canvas({ jobs, batches: [batch([JOB_A])] })
    expect(markup).toContain('待确认')
    expect(markup).not.toContain('已完成')
    expect(markup).toContain('有 1 张候选图等着采用')
  })

  it('同节点还有任务在跑时，候选不覆盖「生成中」', () => {
    const jobs = [
      job({ jobId: JOB_A, state: 'Running', targetNodeId: NODE, progressPercent: 5 }),
      job({ jobId: JOB_B, state: 'Succeeded', targetNodeId: NODE, attachmentState: 'none' })
    ]
    const markup = canvas({ jobs, batches: [batch([JOB_B])] })
    // 节点那一格仍是「生成中」（那条任务），候选不把它顶掉。
    expect(markup).toContain('这个节点上有 1 个生成任务正在进行')
    expect(markup).not.toContain('待确认')
  })

  it('候选卡与它的按钮都不成为正式节点：画布里没有第二个 data-record-id', () => {
    const jobs = [job({ jobId: JOB_B, state: 'Succeeded', targetNodeId: NODE, attachmentState: 'none' })]
    const markup = canvas({ jobs, batches: [batch([JOB_B])] })
    expect(markup.match(/data-record-id=/g) ?? []).toHaveLength(1)
    // 卡片整层在节点层之外，且逐张拦住按下（不会被当成拖节点或连线起手）。
    expect(markup).toContain('df-candidate-layer')
  })

  it('原节点不在画布上时整批不画（与 Desktop 的 ghost 一样，不另找地方摆）', () => {
    const jobs = [job({ jobId: JOB_B, state: 'Succeeded', targetNodeId: OTHER, attachmentState: 'none' })]
    const markup = canvas({
      records: [record(NODE, '镜头一')],
      jobs,
      batches: [batch([JOB_B], { nodeId: OTHER })]
    })
    expect(markup).not.toContain('df-candidate-card')
  })
})
