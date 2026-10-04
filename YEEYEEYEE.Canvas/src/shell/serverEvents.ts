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

/** 有人上线 / 下线。载荷只给原因与是谁，名单仍要自己去取（与 edits.changed 同形）。 */
export type PresenceChangedEvent = {
  type: 'presence.changed'
  reason: string
  actor: string
  at: string
}

export type ServerEvent = CanvasChangedEvent | EditsChangedEvent | PresenceChangedEvent

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

  if (kind === 'presence.changed')
    return { type: 'presence.changed', reason: text(item.reason, 'unknown'), actor: text(item.actor, '有人'), at }

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
 * 「这条推送说的修订，和我手上的不是一个吗」。
 *
 * 相等**不算**：自己保存成功之后服务端也会广播一条，修订正好等于手上的，那不是「别人改了」。
 *
 * 但**不能比大小**。项目模式的修订号是画布内容的哈希（服务端拿它做 CAS），
 * 新内容的哈希不保证比旧的大——用 `>` 判断的话，大约一半的推送会被静默丢掉，
 * 而它不报错，只是那枚提示永远不出现。所以判据是「不一样」：不一样就说明我手上这份不是最新的。
 * 至于谁更新，哈希本来就排不出来，界面也不该假装知道——所以按钮给的是「重新加载」，不是「合并」。
 */
export function isOtherRevision(mine: number | undefined, event: CanvasChangedEvent): boolean {
  return typeof mine === 'number' && event.revision !== mine
}

/** 把事件说成一句人话。节点标题由调用方给（它手上有整份记录）。 */
export function canvasChangeText(event: CanvasChangedEvent, recordTitle?: string): string {
  if (event.scope === 'layout') return `${event.actor} 重新整理了画布布局`
  if (event.recordId === null) return `${event.actor} 改动了画布`
  return recordTitle
    ? `${event.actor} 改动了「${recordTitle}」`
    : `${event.actor} 改动了 1 个节点`
}

/** 收到「画布有变动」之后该做什么。 */
export type FollowAction = 'ignore' | 'notify' | 'reload'

/**
 * 收到「画布有变动」之后：本地干净就**自动跟上**，手上有草稿就只提示。
 *
 * 判据与桌面端（`RemoteCanvasChange.Decide`）是同一条：自动跟上会**替换手上这份**，
 * 所以只在「手上确实没有没提交的东西」时才做。有草稿时给的是提示与按钮——把决定权留给用户，
 * 而不是替他丢掉刚敲进去的那几行。
 */
export function followAction(isStale: boolean, hasDraft: boolean): FollowAction {
  if (!isStale) return 'ignore'
  return hasDraft ? 'notify' : 'reload'
}

/**
 * 「手上有没有还没提交的东西」：编辑框聚焦中，或者内容已经和记录不一样了。
 *
 * 两个都要看：聚焦中但是还没改（点进去看了一眼）算「有可能要改」，
 * 而**改完没保存就点了别处**（失焦）的那种，只有内容比对才能发现。
 */
export function hasUnsavedDraft(
  focused: boolean,
  current: { title: string; content: string },
  record: { title: string; content: string } | null
): boolean {
  if (focused) return true
  if (record === null) return false
  return current.title !== record.title || current.content !== record.content
}

/**
 * 在线名单里的一项（`GET /api/web/presence` 的读侧投影）。
 *
 * `basis` 如实说明这个人是靠哪条依据算出来的：
 * · `connection`：有一条活着的 SSE 连接（此刻在线的主依据），`connections` 是他的连接数；
 * · `recent`：会话表 `last_seen_at` 还在 TTL 内（桌面的兜底、或网页端刚断），此时没有连接数。
 * 两种依据不合并：`connection` 说「现在」，`recent` 只能说「刚还在」。
 */
export type PresencePerson = {
  userId: string
  displayName: string
  clients: string[]
  connections: number
  basis: 'connection' | 'recent' | string
  lastSeenSeconds: number
}

export type PresenceSnapshot = {
  people: PresencePerson[]
  lifetimeSeconds: number
}

const EMPTY_PRESENCE: PresenceSnapshot = { people: [], lifetimeSeconds: 0 }

/** 解析在线名单。形状不对的项**跳过而不是编一个**——名单错一个名字比少一个更糟。 */
export function parsePresence(raw: unknown): PresenceSnapshot {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return EMPTY_PRESENCE
  const item = raw as Record<string, unknown>
  const people: PresencePerson[] = []
  if (Array.isArray(item.people)) {
    for (const entry of item.people) {
      if (!entry || typeof entry !== 'object' || Array.isArray(entry)) continue
      const person = entry as Record<string, unknown>
      const userId = text(person.userId)
      if (userId.length === 0) continue
      people.push({
        userId,
        displayName: text(person.displayName, '有人'),
        clients: Array.isArray(person.clients)
          ? person.clients.filter((client): client is string => typeof client === 'string')
          : [],
        connections: typeof person.connections === 'number' && Number.isFinite(person.connections)
          ? person.connections
          : 0,
        basis: text(person.basis, 'recent'),
        lastSeenSeconds: typeof person.lastSeenSeconds === 'number' && Number.isFinite(person.lastSeenSeconds)
          ? person.lastSeenSeconds
          : 0
      })
    }
  }
  return {
    people,
    lifetimeSeconds: typeof item.lifetimeSeconds === 'number' && Number.isFinite(item.lifetimeSeconds)
      ? item.lifetimeSeconds
      : 0
  }
}
