import { layerOf, isUuid, type OperationRecord } from '../Protocol/VersionedMessages'
import {
  chapterIdOf, chapterOrderOf, isPlanningLayer, sortWithinChapters, type ViewRecord
} from '../ChapterView'

/**
 * 画布记录（协议层的 OperationRecord）的读取口径。
 *
 * 这里全部是纯函数，没有 React：服务端投影出来的字段名、章节归属、排序与分组规则
 * 都是**可测**的东西，放在组件里就没法单独钉住。组件只负责画。
 *
 * 一条硬约束：`record.recordId` 是稳定身份，`record.title` / `record.name` 只是显示标签。
 * 按名字做键会在同名章节/同名实体上悄悄合并——ChapterView 里已经因为这条踩过坑。
 */

export type { ViewRecord }

/** 服务端投影出来的节点种类（NodeProjection.CategoryToRecordType 的九个取值）。 */
export type NodeKind = 'plan' | 'outline' | 'chapter' | 'storyboard' | 'product' | 'character' | 'scene' | 'prop' | 'general'

/**
 * 种类色带。值与 Desktop.Shared/Agent/NodeKindPalette.cs 逐个对齐（那边是 UI-free 的唯一口径，
 * 九个种类九种颜色）。换成自己的一套色只会让两端对同一张画布给出不同的读法。
 */
export const NODE_KINDS: Record<NodeKind, { label: string; hex: string; layer: number }> = {
  plan: { label: '故事企划', hex: '#A78BFA', layer: 1 },
  outline: { label: '故事大纲', hex: '#818CF8', layer: 2 },
  chapter: { label: '章节', hex: '#4D9BFF', layer: 3 },
  storyboard: { label: '分镜', hex: '#2DD4BF', layer: 4 },
  product: { label: '成品', hex: '#E879F9', layer: 5 },
  character: { label: '出场角色', hex: '#FB923C', layer: 4 },
  scene: { label: '场景', hex: '#4ADE80', layer: 4 },
  prop: { label: '道具', hex: '#A3E635', layer: 4 },
  general: { label: '通用', hex: '#8FA6BD', layer: 0 }
}

export function kindOf(recordType: string): NodeKind {
  const t = recordType.toLowerCase()
  if (t.includes('story') && t.includes('plan')) return 'plan'
  if (t.includes('story') && (t.includes('outline') || t.includes('企划'))) return 'outline'
  if (t.includes('chapter') || t.includes('章节')) return 'chapter'
  if (t.includes('storyboard') || t.includes('分镜') || t.includes('shot')) return 'storyboard'
  if (t.includes('product') || t.includes('成品') || t.includes('video')) return 'product'
  if (t.includes('character') || t.includes('角色')) return 'character'
  if (t.includes('scene') || t.includes('场景')) return 'scene'
  if (t.includes('prop') || t.includes('道具')) return 'prop'
  return 'general'
}

export function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback
}

export function numberOf(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

export function recordTitle(item: OperationRecord): string {
  const title = text(item.record.title, text(item.record.name))
  return title.length > 0 ? title : item.recordType
}

export function recordContent(item: OperationRecord): string {
  return text(item.record.content, text(item.record.text))
}

export function recordStatus(item: OperationRecord): string {
  return text(item.record.status, '—')
}

export function nodeX(item: OperationRecord): number {
  return numberOf(item.record.x) ?? 0
}

export function nodeY(item: OperationRecord): number {
  return numberOf(item.record.y) ?? 0
}

/**
 * 这条记录是不是「画布上的真实节点」。
 * 工作树章节行（NodeProjection 用 `wt-<id>` 作为 recordId）没有稳定 GUID，也不参与画布编辑，
 * 它们只用于给章节提供名称与顺序，不能被点开去改标题内容。
 */
export function isEditableRecord(item: OperationRecord): boolean {
  return isUuid(item.recordId)
}

function looksLikeRecord(value: unknown): value is ViewRecord {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false
  const item = value as Partial<OperationRecord>
  return typeof item.recordId === 'string' && item.recordId.trim().length > 0
    && typeof item.recordType === 'string'
    && !!item.record && typeof item.record === 'object' && !Array.isArray(item.record)
}

export type ShellScene = {
  revision: number
  records: ViewRecord[]
  /** 服务端判定这张画布只能读（高版本格式、校验有错、迁移有歧义）。独立场景模式不带这个字段。 */
  readOnly: boolean
  formatVersion?: number
  /** 项目名与画布名。独立场景模式的服务端不投影这两个字段，缺省时由调用方给一个中性的兜底。 */
  projectName?: string
  canvasTitle?: string
}

/**
 * 校验 `/api/web/scene` 的响应。
 *
 * 服务端在项目模式下会回 `{revision, records, readOnly, formatVersion, projectName, canvasTitle, migration, validation}`，
 * 独立场景模式只有 `{revision, records}`。两种情况都收，但**只认自己校验过的字段**：
 * 拿一个形状不对的响应继续往后画，界面会显示一张看起来正常、实际是错的画布。
 */
export function parseScene(value: unknown): ShellScene {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('响应格式不正确')
  const raw = value as { revision?: unknown; records?: unknown; readOnly?: unknown; formatVersion?: unknown; projectName?: unknown; canvasTitle?: unknown }
  const revision = numberOf(raw.revision)
  if (revision === undefined || revision < 0) throw new Error('响应格式不正确')
  if (!Array.isArray(raw.records) || !raw.records.every(looksLikeRecord)) throw new Error('响应格式不正确')
  const projectName = text(raw.projectName).trim()
  const canvasTitle = text(raw.canvasTitle).trim()
  return {
    revision,
    records: raw.records,
    readOnly: raw.readOnly === true,
    formatVersion: numberOf(raw.formatVersion),
    projectName: projectName.length > 0 ? projectName : undefined,
    canvasTitle: canvasTitle.length > 0 ? canvasTitle : undefined
  }
}

/** 企划层不进章节泳道（它们本来就在章节之外），与桌面端的泳道语义一致。 */
export const PLANNING_GROUP_ID = '__planning__'

/** 未归档：有稳定身份，但没有挂到任何已知章节上的节点。 */
export const UNFILED_GROUP_ID = '__unfiled__'

/**
 * 是不是企划层。规则在 `ChapterView.isPlanningLayer` 那一份（确切的两层：L1 剧情、L2 企划）。
 *
 * 这里原本另写了一份，因为那时 ChapterView 写的是 `layerOf(...) <= 2`，而 `layerOf` 对
 * 「认不出来的类型」返回 0，于是角色 / 场景 / 道具 / 通用这些真实节点都会被判成企划层
 * ——章节筛选下永远留在画面上，剧本视图里还会被排到最前。那份判据后来修好了，
 * 所以这里改成直接用它：**同一条规则不留第二份**，否则下一次修的时候只会修一处。
 */
export function isPlanning(record: ViewRecord): boolean {
  return isPlanningLayer(record)
}

export type ChapterGroup = {
  id: string
  label: string
  order: number
  /** 章节自己的说明文字（章节节点或工作树条目的 content）。它是泳道抬头，不是泳道里的节点。 */
  body: string
  records: ViewRecord[]
}

/**
 * 按章节给节点分组。
 *
 * 章节可能有两种来源：画布上的章节节点，以及工作树里没有对应节点的章节条目
 * （NodeProjection 会把后者补成 `wt-<id>` 记录）。两者都以 `workTreeItemId ?? chapterId` 为身份，
 * 所以这里先收章节定义、再把节点挂上去，避免「有章节但没节点」的章节在时间轴上整条消失。
 *
 * 章节定义本身**不算**泳道里的节点：它的标题就是泳道抬头，它自己的文字挂在 `body` 上。
 * 否则时间轴上会出现一条抬头叫「第一章」、下面第一张卡也叫「第一章」的重复。
 */
export function chapterGroups(records: ViewRecord[]): ChapterGroup[] {
  const definitions = new Map<string, { id: string; label: string; order: number; body: string }>()
  for (const record of records) {
    if (kindOf(record.recordType) !== 'chapter') continue
    // 章节节点与工作树行都以 workTreeItemId 为身份；节点那份退回到它自己解析出来的 chapterId。
    const key = text(record.record.workTreeItemId, chapterIdOf(record))
    if (key.length === 0 || definitions.has(key)) continue
    definitions.set(key, { id: key, label: recordTitle(record), order: chapterOrderOf(record), body: recordContent(record) })
  }

  const planning = records.filter((record) => isEditableRecord(record) && isPlanning(record))
  const chapters = [...definitions.values()].sort((left, right) =>
    left.order !== right.order ? left.order - right.order
      : left.label !== right.label ? (left.label < right.label ? -1 : 1)
        : left.id < right.id ? -1 : left.id > right.id ? 1 : 0)

  const groups: ChapterGroup[] = []
  if (planning.length > 0) {
    groups.push({ id: PLANNING_GROUP_ID, label: '企划', order: -1, body: '', records: sortWithinChapters(planning) })
  }
  for (const definition of chapters) {
    const owned = records.filter((record) =>
      isEditableRecord(record) && kindOf(record.recordType) !== 'chapter' && chapterIdOf(record) === definition.id)
    groups.push({ ...definition, records: sortWithinChapters(owned) })
  }

  // 未归档：没挂到任何已知章节上的节点。指向一个已经不存在的章节时也一样——
  // 这类节点在界面上彻底消失，比归到「未归档」难查得多。
  const known = new Set(chapters.map((chapter) => chapter.id))
  const orphans = records.filter((record) =>
    isEditableRecord(record) && kindOf(record.recordType) !== 'chapter' && !isPlanning(record) && !known.has(chapterIdOf(record)))
  if (orphans.length > 0) {
    groups.push({ id: UNFILED_GROUP_ID, label: '未归档', order: Number.MAX_SAFE_INTEGER, body: '', records: sortWithinChapters(orphans) })
  }

  return groups
}

export type ScriptEntry = {
  id: string
  kind: 'chapter' | 'node'
  title: string
  body: string
  kindLabel: string
  hex: string
  meta: string
}

/**
 * 剧本视图的条目：把章节与它下面的文字按顺序摊开。
 * 排序交给 sortWithinChapters（工作树显式顺序 → 坐标 → 标题），保证同一份数据每次渲染顺序一致。
 */
export function scriptEntries(records: ViewRecord[]): ScriptEntry[] {
  const entries: ScriptEntry[] = []
  for (const group of chapterGroups(records)) {
    if (group.id === PLANNING_GROUP_ID) {
      for (const record of group.records) entries.push(toEntry(record))
      continue
    }
    entries.push({
      id: `chapter:${group.id}`,
      kind: 'chapter',
      title: group.label,
      body: group.body,
      kindLabel: '章节',
      hex: NODE_KINDS.chapter.hex,
      meta: `${group.records.length} 个节点`
    })
    for (const record of group.records) entries.push(toEntry(record))
  }
  return entries
}

function toEntry(record: ViewRecord): ScriptEntry {
  const kind = kindOf(record.recordType)
  return {
    id: record.recordId,
    kind: 'node',
    title: recordTitle(record),
    body: recordContent(record),
    kindLabel: NODE_KINDS[kind].label,
    hex: NODE_KINDS[kind].hex,
    meta: recordStatus(record)
  }
}

export type StageSummary = { key: string; label: string; hex: string; count: number }

/**
 * 制作阶段芯片的计数。
 *
 * 桌面端那份计数来自 ProductionStageRules.Summarize（在共享层，网页端以后可以直接复用同一份结果），
 * 这里先按层级给一版**真实**计数：只数可编辑节点，不数工作树章节行，否则章节会被算两遍。
 * 不编造完成度——编出来的勾叉比空着更糟。
 */
export function stageSummaries(records: ViewRecord[]): StageSummary[] {
  const stages: Array<{ key: string; label: string; hex: string; match: (layer: number) => boolean }> = [
    { key: 'planning', label: '企划', hex: NODE_KINDS.plan.hex, match: (layer) => layer === 1 || layer === 2 },
    { key: 'chapter', label: '章节', hex: NODE_KINDS.chapter.hex, match: (layer) => layer === 3 },
    { key: 'storyboard', label: '分镜', hex: NODE_KINDS.storyboard.hex, match: (layer) => layer === 4 },
    { key: 'product', label: '成品', hex: NODE_KINDS.product.hex, match: (layer) => layer === 5 }
  ]
  return stages.map((stage) => ({
    key: stage.key,
    label: stage.label,
    hex: stage.hex,
    count: records.filter((record) => isEditableRecord(record) && stage.match(layerOf(record.recordType))).length
  }))
}

/** 世界画布尺寸：装得下所有节点，且不小于桌面端那张 4200×3000。 */
export function canvasBounds(records: ViewRecord[]): { width: number; height: number } {
  let width = 4200
  let height = 3000
  for (const record of records) {
    if (!isEditableRecord(record)) continue
    width = Math.max(width, nodeX(record) + 600)
    height = Math.max(height, nodeY(record) + 400)
  }
  return { width, height }
}
