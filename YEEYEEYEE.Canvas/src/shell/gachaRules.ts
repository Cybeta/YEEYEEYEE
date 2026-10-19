import type { CandidateSlotView } from './candidateBatch'
import { qualitySelectionLine, type JobQuality } from './imageQuality'

/**
 * 网页端「开奖」的纯计算：批次能不能开、这一手牌有哪几张、牌怎么摆、动画走多久。
 *
 * 与 Desktop 的 GachaRevealDialog / GachaCardLayout 同一套规则与常数。放在这里而不是组件里，
 * 是因为排布与终态判断错了只会表现为「看起来差不多」——那种问题只能靠断言钉住。
 *
 * 判档结果随牌一起带着（`GachaCard.quality`）：**没有结果就是没判过**，
 * 界面按 `imageQuality` 里那套与 Desktop 一致的文案显示，不在这一层编档位。
 */

/** 浏览器偏好：开奖动画是否启用。默认关（与 Desktop 的默认一致，升级上来先看到原来的流程）。 */
export const GACHA_PREF_KEY = 'yeeeyee.web.gachaReveal'

type PreferenceStore = Pick<Storage, 'getItem' | 'setItem'>

function browserStore(): PreferenceStore | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage
  } catch {
    return null
  }
}

export function gachaPreferenceFrom(raw: string | null): boolean {
  return raw === 'on' || raw === 'true' || raw === '1'
}

export function readGachaPreference(store: PreferenceStore | null = browserStore()): boolean {
  try {
    return gachaPreferenceFrom(store?.getItem(GACHA_PREF_KEY) ?? null)
  } catch {
    return false
  }
}

export function writeGachaPreference(value: boolean, store: PreferenceStore | null = browserStore()): void {
  try {
    store?.setItem(GACHA_PREF_KEY, value ? 'on' : 'off')
  } catch {
    // 浏览器禁用存储时偏好只活这一次会话；开奖本身不依赖它。
  }
}

export type GachaBatchProgress = {
  total: number
  succeeded: number
  failed: number
  cancelled: number
  /** 还在排队 / 生成中的槽位。 */
  pending: number
  /** 整批都已进入成功、失败或取消终态。 */
  terminal: boolean
  /** 有成功、且至少一张还没采用（能进揭晓）。 */
  revealable: number
  canReveal: boolean
  allFailed: boolean
}

/**
 * 整批是否都已落终态，以及能不能开奖。
 *
 * 「能开」= 全部落终态 **且** 至少有一张成功且未采用。部分失败照样能开：
 * 失败的那几格在候选区与揭晓页各自如实说明，不伪装成成功卡。
 */
export function gachaBatchProgress(slots: CandidateSlotView[]): GachaBatchProgress {
  let succeeded = 0
  let failed = 0
  let cancelled = 0
  let pending = 0
  let revealable = 0
  for (const slot of slots) {
    if (slot.status === 'succeeded') {
      succeeded++
      if (slot.adoptable && slot.imageUrl) revealable++
    } else if (slot.status === 'failed') failed++
    else if (slot.status === 'cancelled') cancelled++
    else pending++
  }
  const terminal = pending === 0
  return {
    total: slots.length,
    succeeded,
    failed,
    cancelled,
    pending,
    terminal,
    revealable,
    canReveal: terminal && revealable > 0,
    allFailed: terminal && slots.length > 0 && succeeded === 0
  }
}

export type GachaCard = {
  /** 与候选批次里的槽位同一个稳定 key。 */
  key: string
  jobId: string
  outputIndex: number
  imageUrl: string
  /** 候选区里的序号（1 起，失败/取消也占号），揭晓里沿用它。 */
  number: number
  /** 服务端按「任务 + 产物下标」给的判档结果；null = 没判过。 */
  quality: JobQuality | null
}

export type GachaFailure = {
  number: number
  label: string
  reason: string
}

export type GachaDeck = {
  cards: GachaCard[]
  failed: GachaFailure[]
  cancelled: number[]
}

/** 只把成功、未采用的图摆进揭晓；失败与取消单列，绝不混进牌堆。 */
export function gachaDeck(slots: CandidateSlotView[]): GachaDeck {
  const cards: GachaCard[] = []
  const failed: GachaFailure[] = []
  const cancelled: number[] = []
  slots.forEach((slot, index) => {
    const number = index + 1
    if (slot.status === 'succeeded') {
      if (slot.adoptable && slot.imageUrl)
        cards.push({
          key: slot.key, jobId: slot.jobId, outputIndex: slot.outputIndex, imageUrl: slot.imageUrl, number,
          quality: slot.quality
        })
      return
    }
    if (slot.status === 'failed') failed.push({ number, label: slot.label, reason: slot.failure })
    if (slot.status === 'cancelled') cancelled.push(number)
  })
  return { cards, failed, cancelled }
}

export function gachaStatusLine(progress: GachaBatchProgress): string {
  const parts = [`共 ${progress.total} 张`, `成功 ${progress.succeeded}`]
  if (progress.failed > 0) parts.push(`失败 ${progress.failed}`)
  if (progress.cancelled > 0) parts.push(`已取消 ${progress.cancelled}`)
  if (progress.pending > 0) parts.push(`等待 ${progress.pending}`)
  return parts.join(' · ')
}

/**
 * 揭晓里的时序（毫秒），逐项照 Desktop 的 GachaRevealDialog：
 * 蓄势 → 爆发 → 飞卡 → 依次翻面 → 可以挑。
 */
export const GACHA_TIMING = {
  burstMs: 820,
  cardsFlyAtMs: 1250,
  cardFlyMs: 520,
  cardFlyStaggerMs: 90,
  settleHoldMs: 200,
  flipStaggerMs: 170,
  flipHalfMs: 110,
  flipBackMs: 170,
  allDonePadMs: 40
} as const

export function gachaTimeline(count: number): { flipsAt: number; allDoneAt: number } {
  const safe = Math.max(1, count)
  const flipsAt = GACHA_TIMING.cardsFlyAtMs
    + (safe - 1) * GACHA_TIMING.cardFlyStaggerMs
    + GACHA_TIMING.cardFlyMs
    + GACHA_TIMING.settleHoldMs
  const allDoneAt = flipsAt
    + (safe - 1) * GACHA_TIMING.flipStaggerMs
    + GACHA_TIMING.flipHalfMs
    + GACHA_TIMING.flipBackMs
    + GACHA_TIMING.allDonePadMs
  return { flipsAt, allDoneAt }
}

/** 与 Desktop 的 GachaCardLayout 同一套排布：一行最多 3 张；4 张走 2×2。 */
export const GACHA_LAYOUT = { aspect: 1.5, gap: 18, rowGap: 30, maxPerRow: 3 } as const

export function gachaPerRow(count: number): number {
  if (count <= 0) return 0
  if (count <= 3) return count
  if (count === 4) return 2
  return GACHA_LAYOUT.maxPerRow
}

export function gachaRows(count: number): number {
  const perRow = gachaPerRow(count)
  return perRow <= 0 ? 0 : Math.ceil(count / perRow)
}

export function gachaCardWidth(count: number): number {
  if (count <= 0) return 0
  if (count === 1) return 300
  if (count === 2) return 260
  if (count === 3) return 240
  return 200
}

/** 第 index 张相对整手牌中心的偏移；飞卡从这里反向飞回原位。 */
export function gachaOffset(count: number, index: number): { x: number; y: number } {
  const perRow = gachaPerRow(count)
  const rows = gachaRows(count)
  if (perRow <= 0 || index < 0 || index >= count) return { x: 0, y: 0 }
  const row = Math.floor(index / perRow)
  const column = index % perRow
  const inRow = Math.min(perRow, count - row * perRow)
  const width = gachaCardWidth(count)
  const height = width * GACHA_LAYOUT.aspect
  return {
    x: (column - (inRow - 1) / 2) * (width + GACHA_LAYOUT.gap),
    y: (row - (rows - 1) / 2) * (height + GACHA_LAYOUT.rowGap)
  }
}

/** 键盘左右选卡：到头就停在边上，不绕回（与 Desktop 的 ApplySelection 一致）。 */
export function gachaNextSelection(current: number | null, delta: number, count: number): number {
  if (count <= 0) return -1
  if (current === null || current < 0 || current >= count) return delta >= 0 ? 0 : count - 1
  return Math.min(count - 1, Math.max(0, current + delta))
}

export function gachaSelectionNote(selection: number | null, cards: GachaCard[]): string {
  if (selection === null || selection < 0 || selection >= cards.length) return '点一张挑走，再点「用这一张」'
  const card = cards[selection]
  return qualitySelectionLine(card.number, cards.length, card.quality)
}

/** 「用这一张」能不能按：必须选了、没在提交、且那张图真的加载出来了。 */
export function canConfirmGacha(input: {
  selection: number | null
  cards: GachaCard[]
  loadFailed: ReadonlySet<string>
  adopting: boolean
}): boolean {
  const { selection, cards, loadFailed, adopting } = input
  if (adopting || selection === null || selection < 0 || selection >= cards.length) return false
  return !loadFailed.has(cards[selection].key)
}

/**
 * 异步采用回包还能不能落到界面上。
 *
 * 回包回来时这一批可能已经被换掉/关掉（或者用户在等待期间又开了一次）：
 * 用「打开时那次 run」与「当前 open 的批次」两道一起认，旧回包一律丢弃。
 */
export function isStaleGachaResult(
  run: number,
  currentRun: number,
  resultBatchId: string,
  openBatchId: string | null
): boolean {
  return run !== currentRun || openBatchId === null || openBatchId !== resultBatchId
}

/** 从图片主色相取一个光晕色；失败时退回品牌蓝（不编造判档色）。 */
export function averageTint(tints: string[], fallback = '#4d9bff'): string {
  let r = 0
  let g = 0
  let b = 0
  let count = 0
  for (const tint of tints) {
    const match = /^#([0-9a-f]{6})$/i.exec(tint.trim())
    if (!match) continue
    const value = Number.parseInt(match[1], 16)
    r += (value >> 16) & 0xff
    g += (value >> 8) & 0xff
    b += value & 0xff
    count++
  }
  if (count === 0) return fallback
  const hex = (value: number) => Math.round(value).toString(16).padStart(2, '0')
  return `#${hex(r / count)}${hex(g / count)}${hex(b / count)}`
}

/**
 * 揭晓层的会话状态。
 *
 * 单独做成 reducer 是为了把三件容易出错的事钉在一处：
 * · **关闭只收弹层，不动候选批次**（批次在 WebCanvasApp 的另一份 state 里）；
 * · **提交期间重复点击不产生第二次采用**（`adopting` + `submitting`）；
 * · **旧回包按 run 丢弃**（关闭、换批、开始新一次提交都会换 run）。
 */
export type GachaSession = {
  open: boolean
  batchId: string
  nodeId: string
  nodeTitle: string
  slots: CandidateSlotView[]
  selection: number | null
  adopting: boolean
  error: string
  /**
   * 打开那一刻的判档状态说明。**随快照固定**：判档结果之后到达时，
   * 这一层不会补档、也不会因为状态变了把说明改掉。
   */
  qualityNote: string
  /** 打开 / 换批 / 开始采用时换号；异步回包只认自己那一次。 */
  run: number
  /** 正在提交的那张卡的 key：同一张连点也不会再开始一次。 */
  submitting: string
}

export const EMPTY_GACHA_SESSION: GachaSession = {
  open: false,
  batchId: '',
  nodeId: '',
  nodeTitle: '',
  slots: [],
  selection: null,
  adopting: false,
  error: '',
  qualityNote: '',
  run: 0,
  submitting: ''
}

export type GachaSessionAction =
  | { type: 'open'; batchId: string; nodeId: string; nodeTitle: string; slots: CandidateSlotView[]; run: number; qualityNote?: string }
  | { type: 'close' }
  /** 批次被换掉 / 项目切换：关掉并作废所有在途回包。 */
  | { type: 'invalidate' }
  | { type: 'select'; index: number }
  | { type: 'beginAdopt'; key: string; run: number }
  | { type: 'adoptSucceeded'; run: number }
  | { type: 'adoptFailed'; run: number; message: string }

export function gachaSessionReducer(state: GachaSession, action: GachaSessionAction): GachaSession {
  switch (action.type) {
    case 'open':
      return {
        open: true,
        batchId: action.batchId,
        nodeId: action.nodeId,
        nodeTitle: action.nodeTitle,
        slots: action.slots,
        selection: null,
        adopting: false,
        error: '',
        qualityNote: action.qualityNote ?? '',
        run: action.run,
        submitting: ''
      }
    case 'close':
      return { ...EMPTY_GACHA_SESSION, run: state.run + 1 }
    case 'invalidate':
      return { ...EMPTY_GACHA_SESSION, run: state.run + 1 }
    case 'select':
      // 提交期间不接受改选：那会让「正在采用哪一张」与画面上的选中对不上。
      if (!state.open || state.adopting) return state
      if (action.index < 0 || action.index >= state.slots.length) return state
      return { ...state, selection: action.index }
    case 'beginAdopt':
      // 已经在提交、或者这一张已经交出去过（连点），都保持原状。
      if (!state.open || state.adopting || state.submitting === action.key) return state
      return { ...state, adopting: true, error: '', run: action.run, submitting: action.key }
    case 'adoptSucceeded':
      if (action.run !== state.run) return state
      return { ...EMPTY_GACHA_SESSION, run: state.run + 1 }
    case 'adoptFailed':
      if (action.run !== state.run) return state
      // 失败时保留候选、选中与这一批的身份；清掉提交闸门，允许处理后重试。
      return { ...state, adopting: false, submitting: '', error: action.message }
    default:
      return state
  }
}
