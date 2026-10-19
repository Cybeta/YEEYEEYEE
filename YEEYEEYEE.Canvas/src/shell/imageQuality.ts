/**
 * 判档结果的读法与卡面文案。规则与 Desktop 的 `QualityJudgement` 同一套：
 * 分数带、档位顺序（红 > 金 > 紫 > 蓝 > 白）、裂纹卡、以及「没判过 ≠ 白档」。
 *
 * 这里**只读服务端给的结果**：档位由服务端按分数换算，前端不自己把分数折成档位，
 * 也不把模型回复原样搬到界面上。
 */

export type QualityTier = 'white' | 'blue' | 'purple' | 'gold' | 'red'
export type QualitySource = 'technical' | 'model'
export type QualityStatus = 'graded' | 'skipped' | 'failed'

/** 服务端按「任务 + 产物下标」保存的一条结果。 */
export type JobQuality = {
  jobId?: string
  outputIndex: number
  status: QualityStatus
  source?: QualitySource | null
  tier?: QualityTier | null
  score?: number | null
  reason?: string
  negativeHits?: string[]
  ruleVersion?: string
  errorCode?: string | null
  model?: string | null
}

export const QUALITY_PREF_KEY = 'yeeeyee.web.judgeImageQuality'

/** 与 Desktop 的 `QualityJudgement.ScoreBandNote` / `ModelDisclaimer` 同一句话。 */
export const QUALITY_SCORE_BAND_NOTE = '按 0–10 分定档：9 分以上红、7–8 金、5–6 紫、3–4 蓝、2 分以下白；踩中负面提示词的一律是裂纹卡。'
export const QUALITY_DISCLAIMER = '档位是模型照着你写的提示词与负面词给的判断，不是客观结论：同一张图换个模型可能换档。'

const TIER_LABELS: Record<QualityTier, string> = {
  white: '白', blue: '蓝', purple: '紫', gold: '金', red: '红'
}

const TIER_COLORS: Record<QualityTier, string> = {
  white: '#C9D2DE', blue: '#7BA8E8', purple: '#B389E8', gold: '#F3C556', red: '#E8756B'
}

/** 与 Desktop 的 `QualityJudgement.SparksFor` 一致。 */
const TIER_SPARKS: Record<QualityTier, number> = {
  white: 3, blue: 5, purple: 7, gold: 9, red: 12
}

/** 没判过时用的光点数：比白档多一个——「不知道」与「判出来的最低档」不是一回事。 */
export const UNGRADED_SPARKS = 4

/** 裂纹色：与五档都不同，它是另一种状态，不是更低的一档。 */
export const CRACK_COLOR = '#8FA6BD'

export function qualityPreferenceFrom(raw: string | null): boolean {
  return raw === 'on' || raw === 'true' || raw === '1'
}

type PreferenceStore = Pick<Storage, 'getItem' | 'setItem'>

function browserStore(): PreferenceStore | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage
  } catch {
    return null
  }
}

/** 默认关：判档会额外调用模型，可能增加费用与等待。 */
export function readQualityPreference(store: PreferenceStore | null = browserStore()): boolean {
  try {
    return qualityPreferenceFrom(store?.getItem(QUALITY_PREF_KEY) ?? null)
  } catch {
    return false
  }
}

export function writeQualityPreference(value: boolean, store: PreferenceStore | null = browserStore()): void {
  try {
    store?.setItem(QUALITY_PREF_KEY, value ? 'on' : 'off')
  } catch {
    // 浏览器禁用存储时偏好只活这一次会话；判档本身不依赖它。
  }
}

/** 一条结果是不是真的判过档（有档位）。跳过、失败、没有记录都不算。 */
export function qualityIsGraded(quality: JobQuality | null | undefined): boolean {
  return quality?.status === 'graded' && !!quality.tier
}

/** 踩中负面提示词 → 裂纹卡。档位照给，卡面裂开，两件事都留着。 */
export function qualityIsCracked(quality: JobQuality | null | undefined): boolean {
  return qualityIsGraded(quality) && (quality?.negativeHits?.length ?? 0) > 0
}

export function qualityTierLabel(tier: QualityTier): string {
  return TIER_LABELS[tier]
}

export function qualityTierColor(tier: QualityTier): string {
  return TIER_COLORS[tier]
}

export type QualityBadge = { text: string; color: string; cracked: boolean; graded: boolean }

/**
 * 卡面右上角那枚小牌。**没判过就写「未判档」**，不拿白档冒充；
 * 跳过与失败也各自有词，不混成「未判档」。
 */
export function qualityBadge(quality: JobQuality | null | undefined): QualityBadge {
  if (!quality) return { text: '未判档', color: '#A9B5C6', cracked: false, graded: false }
  if (quality.status === 'skipped') return { text: '判档已跳过', color: '#A9B5C6', cracked: false, graded: false }
  if (quality.status === 'failed') return { text: '判档失败', color: '#E5A3A3', cracked: false, graded: false }
  if (quality.status !== 'graded' || !quality.tier)
    return { text: '未判档', color: '#A9B5C6', cracked: false, graded: false }

  const cracked = qualityIsCracked(quality)
  const score = typeof quality.score === 'number' ? `${quality.score}/10` : ''
  if (cracked) return { text: score ? `裂纹 ${score}` : '裂纹', color: CRACK_COLOR, cracked: true, graded: true }
  return {
    text: `${qualityTierLabel(quality.tier)}${score ? ` ${score}` : ''}`,
    color: qualityTierColor(quality.tier),
    cracked: false,
    graded: true
  }
}

/** 选中行说明：分数、档位、谁判的、踩中的原词与模型原话一起给，不只说「金卡」。 */
export function qualitySelectionLine(number: number, total: number, quality: JobQuality | null | undefined): string {
  if (qualityIsGraded(quality) && quality?.tier) {
    const who = quality.source === 'model' ? '模型判定' : '本地判定'
    const score = typeof quality.score === 'number' ? ` ${quality.score}/10` : ''
    let header = `第 ${number} 张 · ${qualityTierLabel(quality.tier)}档${score}（${who}）`
    if (qualityIsCracked(quality))
      header += ` · 裂纹，踩中负面词：${(quality.negativeHits ?? []).join('、')}`
    return quality.reason ? `${header}：${quality.reason}` : header
  }
  if (quality?.status === 'skipped') return `已选中第 ${number} 张（共 ${total} 张）· 判档已跳过`
  if (quality?.status === 'failed') return `已选中第 ${number} 张（共 ${total} 张）· 判档失败`
  return `已选中第 ${number} 张（共 ${total} 张）· 未判档`
}

/** 每张卡自己的光点数；没判过用统一常数，不按白档算。 */
export function qualitySparks(quality: JobQuality | null | undefined): number {
  if (!qualityIsGraded(quality) || !quality?.tier) return UNGRADED_SPARKS
  return TIER_SPARKS[quality.tier]
}

/**
 * 预兆：这一批里最高的那一档决定入场强弱。裂纹卡不参与；
 * 全批都判过但只剩裂纹卡时按白档给（一批明确坏掉的图 ≠ 没判过的图）。
 */
export function qualityOmen(qualities: Array<JobQuality | null | undefined>): QualityTier | null {
  let gradedAny = false
  let best: QualityTier | null = null
  for (const quality of qualities) {
    if (!qualityIsGraded(quality) || !quality?.tier) continue
    gradedAny = true
    if (qualityIsCracked(quality)) continue
    if (best === null || tierRank(quality.tier) > tierRank(best)) best = quality.tier
  }
  return gradedAny && best === null ? 'white' : best
}

function tierRank(tier: QualityTier): number {
  return tier === 'red' ? 4 : tier === 'gold' ? 3 : tier === 'purple' ? 2 : tier === 'blue' ? 1 : 0
}

/** 一批判档的运行状态（只用于界面提示；结果本身以服务端返回为准）。 */
export type BatchQualityRun = {
  state: 'running' | 'done' | 'failed' | 'unavailable' | 'skipped'
  message: string
}

export type QualityStartDecision = 'off' | 'pending' | 'graded' | 'wait' | 'unavailable' | 'start'

/**
 * 这一批现在该不该发起判档。做成纯函数是为了钉住三件事：
 * **关着不发请求**、**已经开始过的不重复发**、**没有可读图模型时不发请求只报配置问题**。
 */
export function qualityStartDecision(input: {
  enabled: boolean
  terminal: boolean
  succeeded: number
  revealable: number
  started: boolean
  qualities: Array<JobQuality | null | undefined>
  availability: { available: boolean } | null
}): QualityStartDecision {
  if (!input.enabled) return 'off'
  if (!input.terminal || input.succeeded === 0 || input.revealable === 0) return 'pending'
  if (input.started) return 'pending'
  const needsGrading = input.qualities.some(quality =>
    quality?.status !== 'graded')
  if (!needsGrading) return 'graded'
  if (input.availability === null) return 'wait'
  if (!input.availability.available) return 'unavailable'
  return 'start'
}

/** 从服务端结果里读出「这批有没有模型不可用」——用它决定提示配置还是提示重试。 */
export function qualityHasUnavailable(results: JobQuality[]): boolean {
  return results.some(item => item.status === 'failed' && item.errorCode === 'MODEL_UNAVAILABLE')
}

export function qualityFailureNote(results: JobQuality[]): string {
  const failed = results.filter(item => item.status === 'failed')
  if (failed.length === 0) return ''
  const reason = failed.find(item => item.reason?.trim())?.reason?.trim()
  return reason ? `判档失败：${reason}` : '判档失败；图不受影响，可以跳过判档继续查看与采用。'
}

/** 解析 `/api/web/jobs` 里的一行 quality；读不懂的跳过，不猜档位。 */
export function parseQualities(raw: unknown): JobQuality[] {
  if (!Array.isArray(raw)) return []
  const result: JobQuality[] = []
  for (const entry of raw) {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) continue
    const row = entry as Record<string, unknown>
    const outputIndex = typeof row.outputIndex === 'number' && Number.isInteger(row.outputIndex) && row.outputIndex >= 0
      ? row.outputIndex
      : null
    const status = row.status === 'graded' || row.status === 'skipped' || row.status === 'failed' ? row.status : null
    if (outputIndex === null || status === null) continue
    const tier = row.tier === 'white' || row.tier === 'blue' || row.tier === 'purple'
      || row.tier === 'gold' || row.tier === 'red' ? row.tier : null
    const source = row.source === 'technical' || row.source === 'model' ? row.source : null
    const score = typeof row.score === 'number' && Number.isInteger(row.score) ? row.score : null
    const hits = Array.isArray(row.negativeHits)
      ? row.negativeHits.filter((item): item is string => typeof item === 'string' && item.trim().length > 0)
      : []
    result.push({
      jobId: typeof row.jobId === 'string' ? row.jobId : undefined,
      outputIndex,
      status,
      source,
      tier,
      score,
      reason: typeof row.reason === 'string' ? row.reason : '',
      negativeHits: hits,
      ruleVersion: typeof row.ruleVersion === 'string' ? row.ruleVersion : undefined,
      errorCode: typeof row.errorCode === 'string' ? row.errorCode : null,
      model: typeof row.model === 'string' ? row.model : null
    })
  }
  return result
}
