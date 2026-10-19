import { recordAttachments, type AttachmentSummary } from '../assets'
import { jobOutputUrl } from './assetImages'
import type { JobQuality } from './imageQuality'
import { jobFailureReason, jobOutputAttachmentId, isJobActive, jobStateLabel, type NodeJob } from './nodeMedia'
import type { ViewRecord } from './records'

/**
 * 节点出图的**候选批次**：临时的、只活在内存里的东西。
 *
 * 与 Desktop 的批次/槽位（Desktop.Shared/Canvas/NodeImageBatch.cs）同一条规则：
 * 批次绑在**原节点**上（一个节点同一时刻一批），一个槽位 = 一次生成提交，批次不进正式画布数据、
 * 不进撤销记录，页面重载后就没了（产物文件仍在资源目录里，不会因为这个被删）。
 * 「采用」才把它写进节点，走的是服务端那条带校验的路（`/api/web/jobs/{id}/outputs/{index}/adopt`）。
 *
 * 这个模块**只做纯计算**：不碰 React、不发请求、不改记录。界面拿它算「每张卡显示什么」。
 */

/** 一个槽位：一次提交。 */
export type CandidateSlot = {
  /** 批次内的稳定身份（= 卡片顺序；某个槽位被丢弃也不挪位）。 */
  key: string
  /** 提交成功后拿到的任务 ID；提交还没回来是 null。 */
  jobId: string | null
  /** 提交这一步就失败时的原因（这一类不留「永远等待」的假卡片）。 */
  submitError: string
}

export type CandidateBatch = {
  batchId: string
  /** 原节点。批次归它，采用也只写给它的服务端校验。 */
  nodeId: string
  /** 提交时刻（算耗时用）。 */
  startedAt: number
  slots: CandidateSlot[]
}

/** 本入口一次提交只产出一件（Desktop 也是一个槽位跑一次生成）。 */
export const CANDIDATE_OUTPUT_INDEX = 0

export type CandidateStatus = 'waiting' | 'running' | 'succeeded' | 'failed' | 'cancelled'

export type CandidateSlotView = {
  key: string
  jobId: string
  outputIndex: number
  status: CandidateStatus
  /** 卡片上的状态词。 */
  label: string
  /** 有效进度（0-100）；拿不到就是 null——不编百分比，用耗时顶着。 */
  progress: number | null
  /** 失败原因（提交失败的原因，或任务失败的原因）。空串 = 没失败。 */
  failure: string
  /** 成功、且节点附件里还没有它：等用户采用。 */
  adoptable: boolean
  /** 成功、且节点附件里已经有它（服务端数据为证）：采用过了。 */
  adopted: boolean
  /** 看图地址。**不是节点附件**，所以走任务产物那条接口。 */
  imageUrl: string | null
  /**
   * 这一格的判档结果：服务端按「任务 + 产物下标」给，**没有就是没判过**。
   * 关掉判档开关时界面不拿它出来显示（见 imageQuality 的卡面函数）。
   */
  quality: JobQuality | null
}

export type CandidateBatchView = {
  batchId: string
  nodeId: string
  /** 提交时刻：卡片自己按它算耗时（谁显示谁计时，不让整张画布每秒重画）。 */
  startedAt: number
  slots: CandidateSlotView[]
  /** 一行摘要（与 Desktop 的 StatusLine 同义）。 */
  headline: string
}

/** 卡片尺寸与间距：与 Desktop 的 ghost 一致（92×68、间隔 8、离节点 22）。 */
export const CANDIDATE_CARD = { width: 92, height: 68, gap: 8, lift: 22 }

const clampPercent = (value: number | undefined): number | null =>
  typeof value === 'number' && Number.isFinite(value) ? Math.min(100, Math.max(0, value)) : null

function statusOf(job: NodeJob | undefined): CandidateStatus {
  if (!job) return 'waiting'
  switch (job.state) {
    case 'Queued': return 'waiting'
    case 'Running': return 'running'
    case 'Succeeded': return 'succeeded'
    case 'Failed': return 'failed'
    case 'Cancelled': return 'cancelled'
    default: return 'waiting'
  }
}

/**
 * 一个槽位此刻的样子。
 *
 * 采用的判据是**服务端给的附件**：节点上已经有这件产物的 ID（服务端按 `web-job:{jobId}:{index}`
 * 算出来的那个）才算采用过。本地不另存一份「采用过了」的记忆——那样两边迟早会不一致。
 * 任务还没出现在列表里（刚提交、轮询还没回来）算「排队中」；提交就失败的直接说失败。
 */
export function candidateSlotView(
  slot: CandidateSlot,
  jobs: NodeJob[] | undefined,
  attachments: AttachmentSummary[]
): CandidateSlotView {
  if (slot.submitError.length > 0)
    return {
      key: slot.key, jobId: '', outputIndex: CANDIDATE_OUTPUT_INDEX, status: 'failed', label: '提交失败',
      progress: null, failure: slot.submitError, adoptable: false, adopted: false, imageUrl: null, quality: null
    }
  if (!slot.jobId)
    return {
      key: slot.key, jobId: '', outputIndex: CANDIDATE_OUTPUT_INDEX, status: 'waiting', label: '排队中',
      progress: null, failure: '', adoptable: false, adopted: false, imageUrl: null, quality: null
    }

  const jobId = slot.jobId
  const job = (jobs ?? []).find((item) => item.jobId === jobId)
  const status = statusOf(job)
  // 只有还在跑的任务才谈得上进度；越界的夹进 0～100，缺值/非有限值一律 null。
  const progress = job && isJobActive(job.state) ? clampPercent(job.progressPercent) : null
  const failure = status === 'failed'
    ? (job ? jobFailureReason(job) : '生成失败（服务端没有给出原因）')
    : status === 'succeeded' && job ? jobFailureReason(job) : ''
  const attachmentId = jobOutputAttachmentId(jobId, CANDIDATE_OUTPUT_INDEX)
  const adopted = status === 'succeeded'
    && attachments.some((item) => item.id.trim().toLowerCase() === attachmentId)

  return {
    key: slot.key,
    jobId,
    outputIndex: CANDIDATE_OUTPUT_INDEX,
    status,
    label: status === 'succeeded' ? '已出好' : job ? jobStateLabel(job.state) : '排队中',
    progress,
    failure,
    adoptable: status === 'succeeded' && !adopted,
    adopted,
    imageUrl: status === 'succeeded' ? jobOutputUrl(jobId, CANDIDATE_OUTPUT_INDEX) : null,
    // 按 outputIndex 认，不按 quality 数组的顺序认：一条结果只属于它自己的任务与下标。
    quality: job?.quality?.find(item => item.outputIndex === CANDIDATE_OUTPUT_INDEX) ?? null
  }
}

export function candidateHeadline(slots: CandidateSlotView[]): string {
  const done = slots.filter((slot) => slot.status === 'succeeded').length
  const failed = slots.filter((slot) => slot.status === 'failed').length
  const cancelled = slots.filter((slot) => slot.status === 'cancelled').length
  const parts = [`已出好 ${done}/${slots.length}`]
  if (failed > 0) parts.push(`失败 ${failed}`)
  if (cancelled > 0) parts.push(`已取消 ${cancelled}`)
  return parts.join(' · ')
}

export function candidateBatchView(
  batch: CandidateBatch,
  jobs: NodeJob[] | undefined,
  attachments: AttachmentSummary[]
): CandidateBatchView {
  const slots = batch.slots.map((slot) => candidateSlotView(slot, jobs, attachments))
  return {
    batchId: batch.batchId,
    nodeId: batch.nodeId,
    startedAt: batch.startedAt,
    slots,
    headline: candidateHeadline(slots)
  }
}

/**
 * 每批此刻该显示什么。附件从**记录本身**取（与检查器、画布卡片同一份读法）：
 * 采用过的那张，服务端写进节点的附件就在那里，界面不用自己记。
 */
export function candidateBatchViews(
  batches: CandidateBatch[],
  jobs: NodeJob[] | undefined,
  records: ViewRecord[]
): CandidateBatchView[] {
  const attachmentsOf = new Map<string, AttachmentSummary[]>()
  for (const record of records) attachmentsOf.set(record.recordId, recordAttachments(record))
  return batches.map((batch) =>
    candidateBatchView(batch, jobs, attachmentsOf.get(batch.nodeId) ?? []))
}

/**
 * 候选卡的位置：整排居中挂在节点**上方**，上方放不下（顶到世界边界）就翻到**下方**。
 *
 * 与 Desktop 的 AddBatchGhosts 同一套算法（宽度 92、间隔 8、离节点 22、贴顶 20）——
 * 位置由节点坐标现算，所以节点一移动，卡片自然跟着走，不需要另存一份偏移。
 */
export function candidateCardRects(
  count: number,
  node: { x: number; y: number },
  nodeWidth = 232,
  nodeHeight = 104
): Array<{ left: number; top: number }> {
  const total = count * CANDIDATE_CARD.width + Math.max(0, count - 1) * CANDIDATE_CARD.gap
  const left = node.x + (nodeWidth - total) / 2
  const above = node.y - CANDIDATE_CARD.height - CANDIDATE_CARD.lift
  const top = above < 20 ? node.y + nodeHeight + CANDIDATE_CARD.lift : above
  return Array.from({ length: count }, (_, index) => ({
    left: left + index * (CANDIDATE_CARD.width + CANDIDATE_CARD.gap),
    top
  }))
}
