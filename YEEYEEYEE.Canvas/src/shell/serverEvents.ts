/**
 * 服务端推来的变更事件（纯解析与判断）。
 *
 * 通道是 SSE（`GET /api/web/events`）。推送的语义是「**告诉你变了**」，不是投递数据：
 * 载荷里只有修订号、记录号与是谁，真内容仍然靠 GET 去取。所以这里只做三件事——
 * 解析、判断「是不是比我手上的新」、以及把它说成一句人话。
 *
 * 认不出的类型**直接忽略**，不抛错：服务端以后加新事件时，旧页面不该整条流都断掉。
 */

export type CanvasChangedEvent = {
  type: 'canvas.changed'
  revision: number
  recordId: string | null
  actor: string
  /** `record` 是改了某个节点的标题/内容，`layout` 是整理布局。 */
  scope: string
  at: string
}

export type EditsChangedEvent = {
  type: 'edits.changed'
  reason: string
  actor: string
  at: string
}

export type ServerEvent = CanvasChangedEvent | EditsChangedEvent

function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback
}

export function parseServerEvent(type: string, data: string): ServerEvent | null {
  let raw: unknown
  try { raw = JSON.parse(data) } catch { return null }
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null
  const item = raw as Record<string, unknown>
  // 以 data 里的 type 为准（event: 那一行只是路由用的）；两边不一致时说明有谁搞错了，宁可不认。
  const kind = text(item.type)
  if (kind !== type) return null
  const at = text(item.at)

  if (kind === 'edits.changed')
    return { type: 'edits.changed', reason: text(item.reason, 'unknown'), actor: text(item.actor, '有人'), at }

  if (kind === 'canvas.changed') {
    const revision = item.revision
    if (typeof revision !== 'number' || !Number.isFinite(revision)) return null
    const recordId = text(item.recordId)
    return {
      type: 'canvas.changed',
      revision,
      recordId: recordId.length > 0 ? recordId : null,
      actor: text(item.actor, '有人'),
      scope: text(item.scope, 'record'),
      at
    }
  }
  return null
}

/**
 * 「这条事件说的修订，比我手上的新吗」。
 *
 * 相等**不算**新：自己保存成功之后服务端也会广播一条，修订正好等于手上的，
 * 那不是「别人改了」，把它当成过期会让状态条对着自己闪一下。
 */
export function isStaleRevision(mine: number | undefined, event: CanvasChangedEvent): boolean {
  return typeof mine === 'number' && event.revision > mine
}

/** 把事件说成一句人话。节点标题由调用方给（它手上有整份记录）。 */
export function canvasChangeText(event: CanvasChangedEvent, recordTitle?: string): string {
  if (event.scope === 'layout') return `${event.actor} 重新整理了画布布局`
  if (event.recordId === null) return `${event.actor} 改动了画布`
  return recordTitle
    ? `${event.actor} 改动了「${recordTitle}」`
    : `${event.actor} 改动了 1 个节点`
}
