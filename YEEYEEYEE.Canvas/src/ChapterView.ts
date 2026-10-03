import { layerOf, type OperationRecord } from './Protocol/VersionedMessages'

export type ViewRecord = OperationRecord & { record: Record<string, unknown> }

/** 章节条目：身份是稳定 ID，标签只用于显示（C-4）。 */
export type ChapterEntry = { id: string; label: string; order: number }

/** 未归档（节点没有稳定章节 ID）的保留 ID。 */
export const UNASSIGNED_CHAPTER_ID = ''

/** 「全部章节」的保留 ID。 */
export const ALL_CHAPTERS_ID = '__all__'

function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback
}

function finiteNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

/**
 * 节点的稳定章节 ID。只认投影写入的 `chapterId`（章节工作树条目 ID）；
 * 章节名文本（`chapter`）**不作为身份**，避免同名章节被误绑定（C-4）。
 * 取不到 ID 时返回空串，表示未归档。
 */
export function chapterIdOf(record: ViewRecord): string {
  return text(record.chapterId, text(record.record.chapterId))
}

/** 仅用于显示的章节名文本；缺失时显示「未归档」。 */
export function chapterLabelOf(record: ViewRecord): string {
  const label = text(record.record.chapter, text(record.record.chapterName))
  return label.length > 0 ? label : '未归档'
}

/** 章节内的显式顺序：数字越小越靠前；未指定排到最后。 */
export function chapterOrderOf(record: ViewRecord): number {
  return finiteNumber(record.record.order) ?? Number.MAX_SAFE_INTEGER
}

/**
 * 章节列表：按显式顺序排序（同一顺序再按显示名、最后按 ID，保证确定性），
 * 未归档项排最后。同名不同 ID 的章节各占一条，不合并。
 */
export function chapterEntries(records: ViewRecord[]): ChapterEntry[] {
  const byId = new Map<string, ChapterEntry>()
  for (const record of records) {
    if (layerOf(record.recordType) !== 3 && !record.recordType.toLowerCase().includes('chapter')) continue
    const id = text(record.record.workTreeItemId, chapterIdOf(record))
    if (id.length === 0) continue
    if (byId.has(id)) continue
    byId.set(id, { id, label: text(record.record.title, text(record.record.name, '章节')), order: chapterOrderOf(record) })
  }
  return [...byId.values()].sort((left, right) =>
    left.order !== right.order ? left.order - right.order
      : left.label !== right.label ? (left.label < right.label ? -1 : 1)
        : (left.id < right.id ? -1 : left.id > right.id ? 1 : 0)
  )
}

/**
 * 企划层（L1 剧情 / L2 企划）在章节筛选下始终保留，因为它们位于章节泳道之外。
 *
 * 判据必须是**确切的两层**，不能写成 `layerOf(...) <= 2`：`layerOf` 对「认不出来的类型」返回 0，
 * 于是角色 / 场景 / 道具 / 通用这些真实节点会一起被判成企划层——选一章的时候它们全都留在画面上，
 * 看起来像「这一章里有三个角色」，其实它们只是没被认出来。0 是「不知道」，不是「企划」。
 *
 * 这条规则与桌面端泳道引擎的 `CanvasSwimlaneLayout.IsPlanningCategory`（StoryPlan / StoryOutline）
 * 是同一条；外壳的 `records.isPlanning` 也直接用它，两处规则只有这一份。
 */
export function isPlanningLayer(record: ViewRecord): boolean {
  const layer = layerOf(record.recordType)
  return layer === 1 || layer === 2
}

/**
 * 按稳定章节 ID 过滤。`ALL_CHAPTERS_ID` 返回全部；选中的章节只保留归属该章节的节点，
 * 加上企划层；没有稳定 ID 的节点不会因为名字相同而被算进该章节。
 */
export function filterByChapter(records: ViewRecord[], chapterId: string): ViewRecord[] {
  if (chapterId === ALL_CHAPTERS_ID) return records
  return records.filter((record) => chapterIdOf(record) === chapterId || isPlanningLayer(record))
}

/**
 * 在章节内排序（C-1 的 Web 侧对齐）：同章节先按工作树显式顺序，再按当前坐标，
 * 最后按标题，保证渲染顺序稳定且不依赖名称。
 */
export function sortWithinChapters(records: ViewRecord[]): ViewRecord[] {
  return [...records].sort((left, right) => {
    const leftChapter = chapterIdOf(left)
    const rightChapter = chapterIdOf(right)
    if (leftChapter !== rightChapter) {
      // 未归档（无稳定 ID）永远排在已归章节之后，避免空串把未归档顶到最前。
      if (leftChapter.length === 0) return 1
      if (rightChapter.length === 0) return -1
      return leftChapter < rightChapter ? -1 : 1
    }
    const leftOrder = chapterOrderOf(left)
    const rightOrder = chapterOrderOf(right)
    if (leftOrder !== rightOrder) return leftOrder - rightOrder
    const leftY = finiteNumber(left.record.y) ?? 0
    const rightY = finiteNumber(right.record.y) ?? 0
    if (leftY !== rightY) return leftY - rightY
    const leftX = finiteNumber(left.record.x) ?? 0
    const rightX = finiteNumber(right.record.x) ?? 0
    if (leftX !== rightX) return leftX - rightX
    const leftTitle = text(left.record.title, text(left.record.name))
    const rightTitle = text(right.record.title, text(right.record.name))
    return leftTitle < rightTitle ? -1 : leftTitle > rightTitle ? 1 : 0
  })
}
