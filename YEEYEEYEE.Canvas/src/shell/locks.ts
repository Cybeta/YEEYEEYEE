/**
 * 编辑锁的读取口径（纯函数）。
 *
 * 锁是**会话状态**不是文档内容：它记在画布旁边的 `<画布>.edits.json`，服务端进程是仲裁者。
 * 网页端这边只做两件事：把它解出来、判断「这条记录归哪把锁管」。判断规则写在这里而不是组件里，
 * 是因为它错了的表现是「明明有人在编辑，界面却让人以为可以改」——那必须能单独测。
 */

export type LeaseScope = 'node' | 'tree'
export type LeaseClient = 'web' | 'desktop' | 'unknown'

export type Lease = {
  leaseId: string
  scope: LeaseScope
  /** 节点级锁才是记录 ID；整棵树锁为 null。 */
  targetId: string | null
  userId: string
  displayName: string
  client: LeaseClient
  acquiredAt: string
  expiresAt: string
}

function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback
}

function asScope(value: unknown): LeaseScope {
  return value === 'tree' ? 'tree' : 'node'
}

function asClient(value: unknown): LeaseClient {
  return value === 'desktop' ? 'desktop' : value === 'web' ? 'web' : 'unknown'
}

/** 来源端的显示名。服务端 `EditClient.Label` 的口径一致：认不出的都说「网页端」。 */
export function clientLabel(client: LeaseClient): string {
  return client === 'desktop' ? '桌面端' : '网页端'
}

/** 「陈默（桌面端）」——冲突提示里直接用这一句。 */
export function describeLease(lease: Lease): string {
  return `${lease.displayName}（${clientLabel(lease.client)}）`
}

export type LeaseList = { leases: Lease[]; lifetimeSeconds: number; invalidCount: number }

/**
 * 解一把锁。形状不对（缺身份、节点级却没有目标）就回 null——
 * 一把认不出的锁**不能**当成「有锁」：那会让界面显示一个说不清是谁的占位；
 * 也不能当成「没锁」：所以调用方要把跳过的条数报出来。
 */
export function parseLease(value: unknown): Lease | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null
  const item = value as Record<string, unknown>
  const leaseId = text(item.leaseId)
  const userId = text(item.userId)
  const displayName = text(item.displayName)
  if (leaseId.length === 0 || userId.length === 0 || displayName.length === 0) return null
  const scope = asScope(item.scope)
  const targetId = text(item.targetId)
  if (scope === 'node' && targetId.length === 0) return null
  return {
    leaseId,
    scope,
    targetId: scope === 'node' ? targetId : null,
    userId,
    displayName,
    client: asClient(item.client),
    acquiredAt: text(item.acquiredAt),
    expiresAt: text(item.expiresAt)
  }
}

/**
 * 解析 `GET /api/web/edits`。外层形状不对就抛；单条坏数据只跳过并计数——
 * 一条坏锁把整个协作界面清空，比显示不出来更糟。
 */
export function parseLeases(value: unknown): LeaseList {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('响应格式不正确')
  const raw = value as { leases?: unknown; lifetimeSeconds?: unknown }
  if (!Array.isArray(raw.leases)) throw new Error('响应格式不正确')
  const lifetimeSeconds = typeof raw.lifetimeSeconds === 'number' && Number.isFinite(raw.lifetimeSeconds) && raw.lifetimeSeconds > 0
    ? raw.lifetimeSeconds : 120
  const leases: Lease[] = []
  for (const entry of raw.leases) {
    const lease = parseLease(entry)
    if (lease) leases.push(lease)
  }
  return { leases, lifetimeSeconds, invalidCount: raw.leases.length - leases.length }
}

/** 申请锁被拒时，从 409 的响应体里取出持有者。取不到就回 null（界面显示通用冲突文案）。 */
export function holderOf(body: unknown): Lease | null {
  if (!body || typeof body !== 'object' || Array.isArray(body)) return null
  return parseLease((body as { holder?: unknown }).holder)
}

/** 谁在编辑整棵树（没有则 null）。 */
export function treeLease(leases: Lease[]): Lease | null {
  return leases.find((lease) => lease.scope === 'tree') ?? null
}

/**
 * 只按节点级匹配。卡片上的锁徽标用它——整棵树锁盖住所有节点，
 * 给每张卡都挂一个徽标只会变成一片噪声，那是画布级的事实，交给提示条说。
 */
export function nodeLease(leases: Lease[], recordId: string): Lease | null {
  return leases.find((lease) => lease.scope === 'node' && lease.targetId === recordId) ?? null
}

/**
 * 管着这条记录的锁：节点级优先（它说得更具体），其次整棵树锁（它盖住所有节点）。
 * 返回 null 表示这条记录现在没人锁。
 */
export function leaseCovering(leases: Lease[], recordId: string): Lease | null {
  return leases.find((lease) => lease.scope === 'node' && lease.targetId === recordId)
    ?? leases.find((lease) => lease.scope === 'tree')
    ?? null
}

/** 锁是不是自己持有的——「你在编辑」与「别人在编辑」在界面上是两件事。 */
export function isMine(lease: Lease | null, userId: string | undefined): boolean {
  return !!lease && !!userId && lease.userId === userId
}

/**
 * 该不该为这个节点去占锁。
 *
 * **光选中一个节点不算**：点着看一圈就撒一地锁，等于把别人挡在外面而自己什么也没改。
 * 只有真的打算改——编辑框拿到了焦点，或者草稿还没保存——才去占。
 * 这个判断放在这里而不是组件里，是因为它错了的表现是「别人被无谓地挡住」，
 * 不报错、也很难查。
 */
export function shouldHoldNodeLease(input: { editable: boolean; focused: boolean; dirty: boolean }): boolean {
  return input.editable && (input.focused || input.dirty)
}
