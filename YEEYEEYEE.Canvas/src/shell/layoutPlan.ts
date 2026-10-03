/**
 * 整理布局计划（纯函数）。
 *
 * 计划由**服务端**按桌面端那份 `CanvasSwimlaneLayout` 算出来，网页端不自己算、
 * 也不往上传坐标（见 README「网页端工作台」）。这里只负责把响应解成界面要的形状，
 * 以及把「哪里会动」变成虚影需要的坐标。
 */

export type LayoutScope = 'all' | string

export type LayoutLane = { kind: string; chapterId: string | null; title: string; order: number; recordIds: string[] }
export type LayoutMove = { recordId: string; title: string; from: { x: number; y: number }; to: { x: number; y: number } }
export type LayoutConflict = { recordId: string; code: string; message: string; blocking: boolean }

export type LayoutPlan = {
  wholeCanvas: boolean
  changed: boolean
  blocking: boolean
  requiresConfirmation: boolean
  /** 手动摆放、会被移动的节点：需要用一次「自动布局覆盖」确认才写。 */
  protectedRecordIds: string[]
  lanes: LayoutLane[]
  moves: LayoutMove[]
  conflicts: LayoutConflict[]
  summary: string
}

function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback
}

function numberOf(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

export function parseLayoutPlan(value: unknown): LayoutPlan {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('响应格式不正确')
  const raw = value as Record<string, unknown>
  if (typeof raw.wholeCanvas !== 'boolean' || typeof raw.changed !== 'boolean') throw new Error('响应格式不正确')
  if (!Array.isArray(raw.changes) || !Array.isArray(raw.lanes) || !Array.isArray(raw.conflicts)) throw new Error('响应格式不正确')

  const moves: LayoutMove[] = []
  for (const entry of raw.changes) {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) continue
    const item = entry as Record<string, unknown>
    const recordId = text(item.recordId)
    const fromX = numberOf(item.fromX)
    const fromY = numberOf(item.fromY)
    const toX = numberOf(item.toX)
    const toY = numberOf(item.toY)
    if (recordId.length === 0 || fromX === undefined || fromY === undefined || toX === undefined || toY === undefined) continue
    moves.push({ recordId, title: text(item.title), from: { x: fromX, y: fromY }, to: { x: toX, y: toY } })
  }

  const lanes: LayoutLane[] = []
  for (const entry of raw.lanes) {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) continue
    const item = entry as Record<string, unknown>
    lanes.push({
      kind: text(item.kind, 'Unknown'),
      chapterId: text(item.chapterId).length > 0 ? text(item.chapterId) : null,
      title: text(item.title),
      order: numberOf(item.order) ?? 0,
      recordIds: Array.isArray(item.recordIds) ? item.recordIds.filter((id): id is string => typeof id === 'string') : []
    })
  }

  const conflicts: LayoutConflict[] = []
  for (const entry of raw.conflicts) {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) continue
    const item = entry as Record<string, unknown>
    conflicts.push({
      recordId: text(item.recordId),
      code: text(item.code),
      message: text(item.message),
      blocking: item.blocking === true
    })
  }

  return {
    wholeCanvas: raw.wholeCanvas,
    changed: raw.changed,
    blocking: raw.blocking === true || conflicts.some((conflict) => conflict.blocking),
    requiresConfirmation: raw.requiresConfirmation === true,
    protectedRecordIds: Array.isArray(raw.protectedRecordIds)
      ? raw.protectedRecordIds.filter((id): id is string => typeof id === 'string') : [],
    lanes,
    moves,
    conflicts,
    summary: text(raw.summary)
  }
}

/** 虚影要画的「从哪到哪」。计划里没有被移动的节点不产生虚影。 */
export function ghostMoves(plan: LayoutPlan): LayoutMove[] {
  return plan.moves
}

/** 计划里会动的节点 ID 集合：画布上这些节点的原位置要压暗，让虚影成为焦点。 */
export function movingIds(plan: LayoutPlan): Set<string> {
  return new Set(plan.moves.map((move) => move.recordId))
}

/** 范围的人话名字，用在预览条上。 */
export function scopeLabel(scope: LayoutScope, chapterTitle?: string): string {
  return scope === 'all' ? '整画布' : (chapterTitle ?? '这一章')
}
