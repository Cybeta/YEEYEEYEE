export const CANVAS_VERSION = '0.1.0'

export interface OperationRecord { recordId: string; recordType: string; record: Record<string, unknown>; parentId?: string; chapterId?: string; deleted?: boolean }

export function layerOf(recordType: string): number {
  const t = recordType.toLowerCase()
  if (t.includes('story') && t.includes('plan')) return 1
  if (t.includes('story') && (t.includes('outline') || t.includes('企划'))) return 2
  if (t.includes('chapter') || t.includes('章节')) return 3
  if (t.includes('storyboard') || t.includes('分镜') || t.includes('shot')) return 4
  if (t.includes('product') || t.includes('成品') || t.includes('video')) return 5
  return 0
}

export function isUuid(value: unknown): value is string { return typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value) }
