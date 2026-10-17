import type { AttachmentSummary } from '../assets'
import { nodeStatusOf } from './records'

/**
 * 「这个节点**自己**出了什么」的读取口径：它的产物（图片 / 视频 / 音频）与它身上的生成任务。
 *
 * 为什么单独成模块：卡片与检查器都要用同一份判据——哪张图当缩略图、有几个视频、
 * 这个节点上有没有在跑的任务、任务是失败还是被取消。而「任务归谁」这件事最容易写错：
 * 界面上的选中项每分钟都可能变，任务的目标节点却是**发起那一刻就固定**的（服务端也只认那一份）。
 * 所以下面所有函数都**按 targetNodeId 找任务**，任何时候都不看「当前选中的是谁」。
 *
 * 与 records.ts / assets.ts 同一个态度：坏的那条跳过、不编。这里只做纯读取，不碰 React，也不发请求。
 */

/**
 * 任务的一件产物（`/api/web/jobs` 里 `outputs` 的一行）。
 *
 * `ref` 是**服务端侧**的资源引用（可能是 `asset://x.png`，也可能是绝对路径），
 * 界面不能拿它去读字节——取图要按 `recordId + attachmentId` 走安全接口，
 * 所以这里只留它在，用来数「这个任务出了几件产物」以及它自己的顺序。
 */
export type NodeJobOutput = { role: string; ref: string }

/** `/api/web/jobs` 一行里界面真正会用的字段（WebJob 的结构子集）。 */
export type NodeJob = {
  jobId: string
  /** 任务创建时固定的目标节点。没有它就不是「回挂到某个节点」的任务。 */
  targetNodeId?: string | null
  state: string
  progressPercent?: number
  errorMessage?: string
  /**
   * 产物回挂状态：`none` / `pending` / `attached`，或服务端给的一句拒绝原因
   * （`TARGET_UNAVAILABLE：…` 这类）。它说的是「产物有没有写进节点」，与任务成败是两件事。
   */
  attachmentState?: string
  /** 这个任务的产物，按服务端的顺序。回挂成功后节点上的附件与它**逐个对位**。 */
  outputs?: NodeJobOutput[]
}

/** 任务到这一步不会再变，界面该停了。取消算终态，但它**不是失败**。 */
export const TERMINAL_JOB_STATES = ['Succeeded', 'Failed', 'Cancelled']

export function isJobActive(state: string): boolean {
  return !TERMINAL_JOB_STATES.includes(state)
}

export type NodeMedia = {
  images: AttachmentSummary[]
  videos: AttachmentSummary[]
  audios: AttachmentSummary[]
  others: AttachmentSummary[]
}

/**
 * 把节点的产物按种类摊开。
 *
 * **不重排**：服务端投影时已经按「最近在前」排好（NodeProjection 里 OrderByDescending(AddedAt)），
 * 再排一次就得自己解析时间戳，还会与服务端的顺序打架——卡片取第一张图当缩略图，
 * 那份顺序就是服务端说了算。
 */
export function nodeMedia(attachments: AttachmentSummary[]): NodeMedia {
  const result: NodeMedia = { images: [], videos: [], audios: [], others: [] }
  for (const attachment of attachments) {
    if (attachment.kind === 'Image') result.images.push(attachment)
    else if (attachment.kind === 'Video') result.videos.push(attachment)
    else if (attachment.kind === 'Audio') result.audios.push(attachment)
    else result.others.push(attachment)
  }
  return result
}

/**
 * 一次任务失败的原因；没失败就是空串。
 *
 * 两种失败都要认：任务本身失败（Failed + errorMessage），以及**产物没回挂上**
 * （任务成功但 attachmentState 是一句拒绝原因）。后者最容易漏——任务显示成功了，
 * 节点上却没有图，用户只会看到「什么都没有」。
 * **取消不算失败**：那是用户自己停的，报成错误等于让界面跟他吵架。
 */
export function jobFailureReason(job: NodeJob): string {
  if (job.state === 'Failed') return job.errorMessage?.trim() || '任务失败（服务端没有给出原因）'
  if (job.state !== 'Succeeded') return ''
  const state = job.attachmentState?.trim() ?? ''
  if (state.length === 0 || state === 'none' || state === 'pending' || state === 'attached') return ''
  return state
}

/** 这个节点身上的任务。按 targetNodeId 认，**不看当前选中项**。 */
export function jobsForNode(jobs: NodeJob[] | undefined, nodeId: string): NodeJob[] {
  if (!nodeId) return []
  return (jobs ?? []).filter((job) => job.targetNodeId === nodeId)
}

/**
 * 目标节点已经不在画布上的任务。
 *
 * 判据用**全部**节点，不是筛选后可见的那些：被搜索或阶段芯片藏起来的节点不是被删了，
 * 把它的任务报成「目标不可用」等于凭空造出一条错误。反过来，节点真的没了（已删除、
 * 或者这条任务属于另一张画布）时也不能悄悄归到别的节点上——那正是「任务串节点」。
 */
export function orphanJobs(jobs: NodeJob[] | undefined, nodeIds: string[]): NodeJob[] {
  const known = new Set(nodeIds)
  return (jobs ?? []).filter((job) => !!job.targetNodeId && !known.has(job.targetNodeId))
}

/** 一个节点身上的任务汇总：还在跑的、失败的。多个任务各自成行，谁也不覆盖谁。 */
export type NodeJobState = {
  active: { jobId: string; state: string; progressPercent: number }[]
  failures: { jobId: string; reason: string }[]
}

export function nodeJobState(jobs: NodeJob[] | undefined, nodeId: string): NodeJobState {
  const result: NodeJobState = { active: [], failures: [] }
  for (const job of jobsForNode(jobs, nodeId)) {
    if (isJobActive(job.state)) {
      const percent = job.progressPercent
      result.active.push({
        jobId: job.jobId, state: job.state,
        progressPercent: typeof percent === 'number' && Number.isFinite(percent) ? Math.min(100, Math.max(0, percent)) : 0
      })
    }
    const reason = jobFailureReason(job)
    if (reason) result.failures.push({ jobId: job.jobId, reason })
  }
  return result
}

/** 整张画布上还有没有在跑的任务——轮询快慢看它。 */
export function hasActiveJobs(jobs: NodeJob[] | undefined): boolean {
  return (jobs ?? []).some((job) => isJobActive(job.state))
}

/**
 * 任务已经落终态、但产物**还在回挂路上**（attachmentState 仍是 `pending`）。
 *
 * 为什么要单独认它：产物回挂是任务成功之后服务端另做的一步，
 * 「任务 Succeeded」与「节点上真的多了一件产物」之间有一小段空档。
 * 这段空档里画布该继续按快表盯着——否则用户看到「已完成」却一直等不到那张图。
 */
export function attachmentPending(job: NodeJob): boolean {
  return !isJobActive(job.state) && job.attachmentState?.trim() === 'pending'
}

export function hasPendingAttachments(jobs: NodeJob[] | undefined): boolean {
  return (jobs ?? []).some(attachmentPending)
}

/** SHA-256（FIPS 180-4）的 64 个轮常量。 */
const SHA256_K = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
])

const rotr = (value: number, bits: number): number =>
  ((value >>> bits) | (value << (32 - bits))) >>> 0

/**
 * 一段字节的 SHA-256 摘要。
 *
 * 为什么自己写：产物回挂时服务端是拿 `SHA256("web-job:{jobId}:{index}")` 算出附件 ID 的
 * （见 ProjectCanvasSceneStore.AttachJobOutputs），网页端要**认出**「节点上这一件产物就是
 * 这个任务的产物」只能算同一个值。而 WebCrypto 的 `crypto.subtle.digest` 是异步的，
 * 这里要的是一个能在渲染时同步给出答案的纯函数。
 */
function sha256(bytes: Uint8Array): Uint8Array {
  const state = new Uint32Array([
    0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19
  ])
  const blocks = Math.ceil((bytes.length + 9) / 64)
  const padded = new Uint8Array(blocks * 64)
  padded.set(bytes)
  padded[bytes.length] = 0x80
  const view = new DataView(padded.buffer)
  const bits = bytes.length * 8
  view.setUint32(padded.length - 8, Math.floor(bits / 0x100000000))
  view.setUint32(padded.length - 4, bits >>> 0)

  const w = new Uint32Array(64)
  for (let offset = 0; offset < padded.length; offset += 64) {
    for (let index = 0; index < 16; index++) w[index] = view.getUint32(offset + index * 4)
    for (let index = 16; index < 64; index++) {
      const s0 = rotr(w[index - 15], 7) ^ rotr(w[index - 15], 18) ^ (w[index - 15] >>> 3)
      const s1 = rotr(w[index - 2], 17) ^ rotr(w[index - 2], 19) ^ (w[index - 2] >>> 10)
      w[index] = (w[index - 16] + s0 + w[index - 7] + s1) >>> 0
    }

    let a = state[0], b = state[1], c = state[2], d = state[3]
    let e = state[4], f = state[5], g = state[6], h = state[7]
    for (let index = 0; index < 64; index++) {
      const t1 = (h + (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) + ((e & f) ^ (~e & g)) + SHA256_K[index] + w[index]) >>> 0
      const t2 = ((rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c))) >>> 0
      h = g; g = f; f = e; e = (d + t1) >>> 0
      d = c; c = b; b = a; a = (t1 + t2) >>> 0
    }
    state[0] = (state[0] + a) >>> 0; state[1] = (state[1] + b) >>> 0
    state[2] = (state[2] + c) >>> 0; state[3] = (state[3] + d) >>> 0
    state[4] = (state[4] + e) >>> 0; state[5] = (state[5] + f) >>> 0
    state[6] = (state[6] + g) >>> 0; state[7] = (state[7] + h) >>> 0
  }

  const digest = new Uint8Array(32)
  const digestView = new DataView(digest.buffer)
  for (let index = 0; index < 8; index++) digestView.setUint32(index * 4, state[index])
  return digest
}

/**
 * 服务端为「某个任务的第 index 件产物」算出来的附件 ID。
 *
 * `.NET` 的 `new Guid(byte[16])` 把前 4 字节当**小端** int、接着两个 2 字节小端 short，
 * `ToString()` 再按数值打印——所以字符串的前三段与摘要字节序正好相反，后 8 字节按原序。
 * 拼错不会报错，只会永远对不上节点上的附件（界面上表现为「产物怎么都不出现」），
 * 所以这一段必须与服务端逐字一致。
 */
export function jobOutputAttachmentId(jobId: string, index: number): string {
  const digest = sha256(new TextEncoder().encode(`web-job:${jobId}:${index}`))
  const hex = (value: number, width: number) => value.toString(16).padStart(width, '0')
  const first = (digest[0] | digest[1] << 8 | digest[2] << 16 | digest[3] << 24) >>> 0
  const second = digest[4] | digest[5] << 8
  const third = digest[6] | digest[7] << 8
  const tail = Array.from(digest.subarray(8, 16), (byte) => hex(byte, 2)).join('')
  return `${hex(first, 8)}-${hex(second, 4)}-${hex(third, 4)}-${tail.slice(0, 4)}-${tail.slice(4)}`
}

/** 一件**确认属于这个任务、这个节点**的产物。 */
export type JobOutputAttachment = { attachmentId: string; kind: string; name: string }

/**
 * 这个任务在目标节点上真的挂上了哪些产物。
 *
 * 三道闸门缺一不可：任务成功、服务端说已经回挂、并且这件产物的身份正是服务端为它算出来的那个 ID
 * （从目标节点的附件里认）。少了第三道就等于「任务成功了就把节点上的图都算作它的」——
 * 同一节点上前后两个任务的产物会互相冒充。附件对不上时**什么都不显示**，宁可空着。
 */
export function attachedOutputs(job: NodeJob, attachments: AttachmentSummary[]): JobOutputAttachment[] {
  if (job.state !== 'Succeeded' || job.attachmentState?.trim() !== 'attached') return []
  const result: JobOutputAttachment[] = []
  for (let index = 0; index < (job.outputs?.length ?? 0); index++) {
    const attachmentId = jobOutputAttachmentId(job.jobId, index)
    const found = attachments.find((item) => item.id.trim().toLowerCase() === attachmentId)
    if (found) result.push({ attachmentId: found.id, kind: found.kind, name: found.name })
  }
  return result
}

/** 时间轴上一行任务要显示的东西。 */
export type JobRow = {
  jobId: string
  state: string
  label: string
  hex: string
  /** 还在跑。 */
  active: boolean
  /** 有效进度（0～100）；拿不到有效进度是 null——不编百分比。 */
  progress: number | null
  /** 任务已完成、产物还在回挂路上。 */
  pending: boolean
  /** 失败原因（任务失败，或产物没挂上）。空串 = 没失败。 */
  failure: string
  /** 已经确认挂在这个节点上的产物。 */
  outputs: JobOutputAttachment[]
}

/**
 * 某个节点上的任务行，**一行一个任务**，顺序就是服务端给的顺序。
 *
 * 一行一个：同一节点上并行两个任务、或一个失败一个还在跑，都不能被合成一行——合起来就必然要
 * 从两个任务里挑一个状态显示，另一个的信息就没了。
 */
export function jobRows(
  jobs: NodeJob[] | undefined,
  nodeId: string,
  attachments: AttachmentSummary[]
): JobRow[] {
  return jobsForNode(jobs, nodeId).map((job) => {
    const active = isJobActive(job.state)
    const percent = job.progressPercent
    return {
      jobId: job.jobId,
      state: job.state,
      label: jobStateLabel(job.state),
      hex: jobStateHex(job.state),
      active,
      // 只有还在跑的任务才谈得上进度；缺值、NaN、Infinity 一律不编成 0% 或 100%。
      progress: active && typeof percent === 'number' && Number.isFinite(percent)
        ? Math.min(100, Math.max(0, percent))
        : null,
      pending: attachmentPending(job),
      failure: jobFailureReason(job),
      outputs: attachedOutputs(job, attachments)
    }
  })
}

/** 任务状态的中文（与桌面端的状态文案同一套口径）。认不出的如实回原值，不编。 */
export function jobStateLabel(state: string): string {
  switch (state) {
    case 'Queued': return '排队中'
    case 'Running': return '生成中'
    case 'Succeeded': return '已完成'
    case 'Failed': return '失败'
    case 'Cancelled': return '已取消'
    default: return state.trim() || '未知状态'
  }
}

/**
 * 任务状态的颜色：映射到节点状态那一套色（见 records.nodeStatusOf），
 * 免得同一个「失败」在卡片上是红的、在检查器里是别的颜色。
 */
export function jobStateHex(state: string): string {
  switch (state) {
    case 'Queued': return nodeStatusOf('WaitingForUser').hex
    case 'Running': return nodeStatusOf('Generating').hex
    case 'Succeeded': return nodeStatusOf('Completed').hex
    case 'Failed': return nodeStatusOf('Failed').hex
    default: return nodeStatusOf('Draft').hex
  }
}

/**
 * 读 `/api/web/jobs` 的响应。
 * 一行读不懂就跳过（与 recordAttachments 同一个态度）：少一条任务状态，比整页取不到任务好。
 */
export function parseJobs(raw: unknown): { jobs: NodeJob[]; invalidCount: number } {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) throw new Error('响应格式不正确')
  const list = (raw as { jobs?: unknown }).jobs
  if (!Array.isArray(list)) throw new Error('响应格式不正确')
  const jobs: NodeJob[] = []
  for (const entry of list) {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) continue
    const row = entry as Record<string, unknown>
    const jobId = typeof row.jobId === 'string' ? row.jobId.trim() : ''
    const state = typeof row.state === 'string' ? row.state.trim() : ''
    if (jobId.length === 0 || state.length === 0) continue
    jobs.push({
      jobId, state,
      targetNodeId: typeof row.targetNodeId === 'string' && row.targetNodeId.trim().length > 0 ? row.targetNodeId : null,
      progressPercent: typeof row.progressPercent === 'number' && Number.isFinite(row.progressPercent) ? row.progressPercent : undefined,
      errorMessage: typeof row.errorMessage === 'string' ? row.errorMessage : undefined,
      attachmentState: typeof row.attachmentState === 'string' ? row.attachmentState : undefined,
      outputs: parseOutputs(row.outputs)
    })
  }
  return { jobs, invalidCount: list.length - jobs.length }
}

/**
 * 读一个任务的 `outputs`。
 *
 * **顺序就是身份**：服务端给第 index 件产物算的附件 ID 里带着这个下标（见 attachedOutputs），
 * 所以这里既不能丢行、也不能重排——读不懂的那一行必须**占着它自己的位置**（留一个空的 ref），
 * 否则后面所有产物的下标都会前移一格，全部对不上节点上的附件。
 */
function parseOutputs(value: unknown): NodeJobOutput[] {
  if (!Array.isArray(value)) return []
  return value.map((entry): NodeJobOutput => {
    const row = entry && typeof entry === 'object' && !Array.isArray(entry)
      ? entry as Record<string, unknown>
      : {}
    return {
      role: typeof row.role === 'string' ? row.role : '',
      ref: typeof row.ref === 'string' ? row.ref : ''
    }
  })
}

/**
 * 媒体类型的中文。认不出的如实回原值，不猜——服务端的 `AttachmentKind` 将来多一个取值时，
 * 界面上该显示那个名字，而不是把它硬塞进「其他」。
 */
export function attachmentKindLabel(kind: string): string {
  switch (kind) {
    case 'Image': return '图片'
    case 'Video': return '视频'
    case 'Audio': return '音频'
    case 'Text': return '文本'
    case 'Document': return '文档'
    case 'Other': return '其他'
    default: return kind.trim() || '未知类型'
  }
}

/**
 * 「现在该显示节点自身哪一个产物」的选择：手上挑的那一个**还在这台节点上就留着**，
 * 不在了（被删掉、或者换到了另一个节点）就回到列表第一个——与服务端给的顺序同一条规则
 * （最近在前，见 NodeProjection），也就是默认选最新的那一件。
 *
 * 刷新场景、生成产物追加回挂都会重新投影这个列表，所以这条判断在**渲染时**算，
 * 不存一份会过期的选中值：选中项没变就还是它，被移除就自然落到剩下的第一个。
 */
export type MediaPick = { recordId: string; attachmentId: string }

export function resolveMediaPick(
  media: AttachmentSummary[],
  recordId: string,
  pick: MediaPick | null
): string {
  if (pick && pick.recordId === recordId && media.some(item => item.id === pick.attachmentId))
    return pick.attachmentId
  return media[0]?.id ?? ''
}

/**
 * 一份产物的加载状态：**第几次尝试**，以及失败是发生在哪一次。
 *
 * 为什么把「第几次」记下来：重试要真的重新取一次字节（地址换一个序号，见 assetImages.withRetry），
 * 而上一件产物、上一次尝试的迟到 error 事件不能把**当前**这一次标成失败——
 * 所以失败只记在它自己那一次的序号上，界面只认当前这一次（照旧显示媒体）。
 */
export type MediaLoad = { attempt: number; failure: { attempt: number; reason: string } | null }

export const initialMediaLoad: MediaLoad = { attempt: 0, failure: null }

/** 当前这一次尝试的失败原因；空串 = 没失败（也包含「上一次的失败已被重试顶掉」）。 */
export function mediaFailure(load: MediaLoad): string {
  return load.failure && load.failure.attempt === load.attempt ? load.failure.reason : ''
}

/** 重试：换一个尝试序号——地址跟着变，浏览器才会真的重新发一次请求，而不只是把提示藏掉。 */
export function retryMedia(load: MediaLoad): MediaLoad {
  return { attempt: load.attempt + 1, failure: load.failure }
}

/** 记一次失败。**只认当前这一次尝试**：旧元素迟到的事件原样返回，不会污染当前预览。 */
export function failMedia(load: MediaLoad, attempt: number, reason: string): MediaLoad {
  return attempt === load.attempt ? { attempt: load.attempt, failure: { attempt, reason } } : load
}
