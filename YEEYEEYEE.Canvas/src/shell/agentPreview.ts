import { canvasUrl } from '../api'
import { recordAttachments, recordReferences, type AttachmentSummary, type Reference } from '../assets'
import { ALL_CHAPTERS_ID, chapterIdOf } from '../ChapterView'
import { attachmentKindLabel } from './nodeMedia'
import { chapterGroups, isEditableRecord, kindOf, parseScene, recordTitle, type ShellEdge, type ViewRecord } from './records'

export type AgentCanvasPreview = {
  sessionId: string
  messageId: string
  revision: number
  diff: {
    addedNodes: ViewRecord[]
    updatedNodes: ViewRecord[]
    removedNodes: ViewRecord[]
    addedEdges: ShellEdge[]
    removedEdges: ShellEdge[]
  }
}

export function parseAgentPreview(raw: unknown): AgentCanvasPreview {
  const invalid = () => new Error('Agent 预览格式不正确')
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) throw invalid()
  const value = raw as AgentCanvasPreview
  if (typeof value.sessionId !== 'string' || !value.sessionId.trim()
    || typeof value.messageId !== 'string' || !value.messageId.trim()
    || !Number.isSafeInteger(value.revision) || value.revision < 0
    || !value.diff || typeof value.diff !== 'object' || Array.isArray(value.diff)) throw invalid()
  for (const key of ['addedNodes', 'updatedNodes', 'removedNodes'] as const) {
    const nodes = parseScene({ revision: value.revision, records: value.diff[key] }).records
    if (!nodes.every(node => isEditableRecord(node)
      && typeof node.record.x === 'number' && Number.isFinite(node.record.x)
      && typeof node.record.y === 'number' && Number.isFinite(node.record.y))) throw invalid()
  }
  for (const key of ['addedEdges', 'removedEdges'] as const) {
    const edges = value.diff[key]
    if (!Array.isArray(edges) || edges.some(edge => !edge || typeof edge !== 'object'
      || !['edgeId', 'sourceId', 'targetId'].every(field => {
        const id = edge[field as keyof ShellEdge]
        return typeof id === 'string' && id.trim().length > 0
      }))) throw invalid()
  }
  return {
    sessionId: value.sessionId, messageId: value.messageId, revision: value.revision,
    diff: value.diff
  }
}

export type PreviewChange = 'added' | 'updated' | 'removed'

/** 虚影层上的一条节点：变更类型、投影后的节点本身，以及它的附件 / 引用差异。 */
export type PreviewNodeLayer = { change: PreviewChange; node: ViewRecord; assets: NodeAssetDelta }

/** 只选择服务端差异的可见投影，不应用动作，也不改写正式记录。 */
export function agentPreviewLayer(
  preview: AgentCanvasPreview | null | undefined,
  records: ViewRecord[],
  activeChapter: string,
  matches: (record: ViewRecord) => boolean = () => true
) {
  const nodes: PreviewNodeLayer[] = []
  const edges: { change: 'added' | 'removed'; edge: ShellEdge; from: ViewRecord; to: ViewRecord }[] = []
  if (!preview) return { nodes, edges }
  const { diff } = preview
  // 「变更前」的那一份就是正式场景本身（预检通过的前提是预览修订与当前修订一致），
  // 按 recordId 取——**不按标题或位置找**，同名节点在这里不会被认成同一个。
  const before = new Map(records.map(node => [node.recordId, node]))
  const projected = new Map(records.map(node => [node.recordId, node]))
  for (const node of [...diff.addedNodes, ...diff.updatedNodes]) projected.set(node.recordId, node)
  const context = [...projected.values(), ...diff.removedNodes.filter(node => !projected.has(node.recordId))]
  function visible(node: ViewRecord) {
    if (!isEditableRecord(node) || !matches(node)) return false
    if (activeChapter === ALL_CHAPTERS_ID) return true
    if (kindOf(node.recordType) === 'chapter')
      return (node.record.workTreeItemId ?? chapterIdOf(node)) === activeChapter
    return chapterGroups([...context.filter(item => item.recordId !== node.recordId), node])
      .some(group => group.id === activeChapter && group.records.some(item => item.recordId === node.recordId))
  }
  for (const change of ['added', 'updated', 'removed'] as const)
    for (const node of diff[`${change}Nodes`])
      if (visible(node)) nodes.push({ change, node, assets: nodeAssetDelta(change, node, before.get(node.recordId)) })
  for (const change of ['added', 'removed'] as const) {
    const endpoints = new Map(records.map(node => [node.recordId, node]))
    for (const node of change === 'added' ? [...diff.addedNodes, ...diff.updatedNodes] : diff.removedNodes)
      endpoints.set(node.recordId, node)
    for (const edge of diff[`${change}Edges`]) {
      const from = endpoints.get(edge.sourceId)
      const to = endpoints.get(edge.targetId)
      if (from && to && visible(from) && visible(to)) edges.push({ change, edge, from, to })
    }
  }
  return { nodes, edges }
}

/** 一条附件（节点产物）的差异：`before` / `after` 有一头是 null 就是新增或移除。 */
export type AttachmentDelta = {
  change: PreviewChange
  /** 稳定身份：服务端给的产物 ID。前后两边都按它对上，不按名称、也不按数组位置。 */
  id: string
  before: AttachmentSummary | null
  after: AttachmentSummary | null
  /** 只有 `updated` 才有内容：变了的属性（名称 / 媒体类型 / 来源）。 */
  fields: DeltaField[]
}

/** 一条引用的差异：`key` 是「实体 + 变体 + 版本」的稳定拼法。 */
export type ReferenceDelta = {
  change: PreviewChange
  key: string
  before: Reference | null
  after: Reference | null
  fields: DeltaField[]
}

/** 变了的某个属性：`跟随最新 → 锁定版本 v2` 这种前后对照就是它拼出来的。 */
export type DeltaField = { label: string; before: string; after: string }

/** 一个节点上待生效的附件与引用变化——虚影摘要与审批详情都读这一份。 */
export type NodeAssetDelta = {
  recordId: string
  change: PreviewChange
  title: string
  attachments: AttachmentDelta[]
  references: ReferenceDelta[]
}

const CHANGE_LABELS: Record<PreviewChange, string> = { added: '＋ 新增', updated: '△ 修改', removed: '− 移除' }

export function changeLabel(change: PreviewChange): string {
  return CHANGE_LABELS[change]
}

/**
 * 一条附件（节点产物）的变化：**按产物 ID 对齐**前后两份投影。
 *
 * 为什么不用名称或位置：同名产物完全可能有两个（同一个标题生成两次），
 * 而位置会随着服务端的排序（最近在前）整体挪动——按它们对齐会把「换了一件产物」
 * 显示成「属性改了」，或者把没动的那几件标成新增。
 */
export function attachmentDeltas(before: AttachmentSummary[], after: AttachmentSummary[]): AttachmentDelta[] {
  const result: AttachmentDelta[] = []
  const previous = new Map(before.map(item => [item.id, item]))
  const matched = new Set<string>()
  for (const item of after) {
    const earlier = previous.get(item.id)
    if (!earlier) { result.push({ change: 'added', id: item.id, before: null, after: item, fields: [] }); continue }
    matched.add(item.id)
    const fields = attachmentFields(earlier, item)
    if (fields.length > 0) result.push({ change: 'updated', id: item.id, before: earlier, after: item, fields })
  }
  for (const item of before)
    if (!matched.has(item.id)) result.push({ change: 'removed', id: item.id, before: item, after: null, fields: [] })
  return result
}

function attachmentFields(before: AttachmentSummary, after: AttachmentSummary): DeltaField[] {
  const fields: DeltaField[] = []
  if (before.name !== after.name) fields.push({ label: '名称', before: before.name, after: after.name })
  if (before.kind !== after.kind)
    fields.push({ label: '类型', before: attachmentKindLabel(before.kind), after: attachmentKindLabel(after.kind) })
  if (before.source !== after.source)
    fields.push({ label: '来源', before: before.source || '—', after: after.source || '—' })
  return fields
}

/**
 * 一条引用的变化：先按「实体 + 变体 + 版本」整体对上（对得上且显示字段没变 = 没动），
 * 再按「实体 + 变体」把**跟随模式 / 锁定版本**的变化认成一次修改——
 * 「跟随最新 → 锁定版本 v2」是一处改动，摆成一增一删会让人以为换了个设定。
 * 剩下的才分别是新增与移除。全程按 ID 对齐。
 */
export function referenceDeltas(before: Reference[], after: Reference[]): ReferenceDelta[] {
  const result: ReferenceDelta[] = []
  const pending = before.slice()
  const incoming = after.slice()
  for (let index = incoming.length - 1; index >= 0; index--) {
    const at = pending.findIndex(item => referenceIdentity(item) === referenceIdentity(incoming[index]))
    if (at < 0) continue
    const earlier = pending.splice(at, 1)[0]
    const current = incoming.splice(index, 1)[0]
    const fields = referenceFields(earlier, current)
    if (fields.length > 0) result.push({ change: 'updated', key: referenceIdentity(current), before: earlier, after: current, fields })
  }
  for (let index = incoming.length - 1; index >= 0; index--) {
    const at = pending.findIndex(item => referenceTarget(item) === referenceTarget(incoming[index]))
    if (at < 0) continue
    const earlier = pending.splice(at, 1)[0]
    const current = incoming.splice(index, 1)[0]
    result.push({ change: 'updated', key: referenceIdentity(current), before: earlier, after: current, fields: referenceFields(earlier, current) })
  }
  for (const item of incoming) result.push({ change: 'added', key: referenceIdentity(item), before: null, after: item, fields: [] })
  for (const item of pending) result.push({ change: 'removed', key: referenceIdentity(item), before: item, after: null, fields: [] })
  return result
}

function referenceIdentity(reference: Reference): string {
  return `${referenceTarget(reference)}|${reference.variantVersionId ?? 'latest'}`
}

function referenceTarget(reference: Reference): string {
  return `${reference.entityId}|${reference.variantId ?? ''}`
}

function referenceFields(before: Reference, after: Reference): DeltaField[] {
  const fields: DeltaField[] = []
  if ((before.name ?? '') !== (after.name ?? ''))
    fields.push({ label: '实体', before: referenceLabel(before), after: referenceLabel(after) })
  else if ((before.variantLabel ?? '') !== (after.variantLabel ?? ''))
    fields.push({ label: '变体', before: before.variantLabel || '—', after: after.variantLabel || '—' })
  if ((before.variantVersionId ?? null) !== (after.variantVersionId ?? null))
    fields.push({ label: '版本', before: referenceModeText(before), after: referenceModeText(after) })
  if (!!before.unresolved !== !!after.unresolved)
    fields.push({ label: '解析', before: referenceModeText(before), after: referenceModeText(after) })
  return fields
}

/** 一条引用的显示名：实体名 · 变体名。身份不在这里，它只用来给人看。 */
export function referenceLabel(reference: Reference): string {
  const name = reference.name?.trim() || '未命名设定'
  const variant = reference.variantLabel?.trim()
  return variant ? `${name} · ${variant}` : name
}

/** 引用怎么取媒体：锁定版本 / 跟随最新；解析不到目标时如实说明。 */
export function referenceModeText(reference: Reference): string {
  if (reference.unresolved) return '引用目标缺失'
  return reference.variantVersionId ? `锁定版本 ${lockedVersionText(reference)}` : '跟随最新'
}

/** 锁定版本的显示名：版本列表里同 ID 的那一条（投影里带着）；找不到就说它缺失，不编一个版本号。 */
function lockedVersionText(reference: Reference): string {
  const version = reference.versions?.find(item => item.id === reference.variantVersionId)
  return version?.label || (version?.number != null ? `v${version.number}` : '版本缺失')
}

function nodeAssetDelta(change: PreviewChange, node: ViewRecord, before?: ViewRecord): NodeAssetDelta {
  const earlier = change === 'added' ? undefined : before
  const later = change === 'removed' ? undefined : node
  return {
    recordId: node.recordId,
    change,
    title: recordTitle(node),
    attachments: attachmentDeltas(earlier ? recordAttachments(earlier) : [], later ? recordAttachments(later) : []),
    references: referenceDeltas(earlier ? recordReferences(earlier) : [], later ? recordReferences(later) : [])
  }
}

/**
 * 这批待审批动作里**所有**节点的附件与引用差异，只看有变化的那些。
 *
 * 与画布上的虚影层不同，这里**不按章节 / 阶段筛选**：批准是对整批动作生效的，
 * 详情里漏掉一个节点的变化，用户就可能在没看全的情况下按下批准。
 */
export function agentPreviewAssetDiffs(
  preview: AgentCanvasPreview | null | undefined,
  records: ViewRecord[]
): NodeAssetDelta[] {
  if (!preview) return []
  const before = new Map(records.map(node => [node.recordId, node]))
  const result: NodeAssetDelta[] = []
  for (const change of ['added', 'updated', 'removed'] as const)
    for (const node of preview.diff[`${change}Nodes`]) {
      const delta = nodeAssetDelta(change, node, before.get(node.recordId))
      if (delta.attachments.length > 0 || delta.references.length > 0) result.push(delta)
    }
  return result
}

/** 一个节点的附件 / 引用变化计数，虚影摘要与详情标题都用它。 */
export function assetDeltaCounts(delta: NodeAssetDelta) {
  const count = (list: { change: PreviewChange }[]) => ({
    added: list.filter(item => item.change === 'added').length,
    removed: list.filter(item => item.change === 'removed').length,
    updated: list.filter(item => item.change === 'updated').length
  })
  return { attachments: count(delta.attachments), references: count(delta.references) }
}

/** 「附件 ＋1 −1 △1 · 引用 ＋1」；没有变化时是空串（界面据此不摆这一行）。 */
export function describeNodeAssetDelta(delta: NodeAssetDelta): string {
  const text = (label: string, counts: { added: number; removed: number; updated: number }) => {
    const parts = [counts.added ? `＋${counts.added}` : '', counts.removed ? `−${counts.removed}` : '', counts.updated ? `△${counts.updated}` : '']
      .filter(Boolean)
    return parts.length > 0 ? `${label} ${parts.join(' ')}` : ''
  }
  const counts = assetDeltaCounts(delta)
  return [text('附件', counts.attachments), text('引用', counts.references)].filter(Boolean).join(' · ')
}

/**
 * 预览里某个产物的字节地址。
 *
 * 待提交的产物还没写进正式节点，正式那条按节点取产物的接口自然读不到它；
 * 这条接口让服务端**按这条消息这批待执行动作重新试算一次**，再从试算结果里取——
 * 走的是同一条预检与同一条安全出字节的路，没有为预览放宽任何校验。
 */
export function previewAttachmentFileUrl(sessionId: string, messageId: string, recordId: string, attachmentId: string): string {
  const path = `/api/web/chat/sessions/${encodeURIComponent(sessionId)}/messages/${encodeURIComponent(messageId)}`
    + `/preview/records/${encodeURIComponent(recordId)}/attachments/${encodeURIComponent(attachmentId)}/file`
  return canvasUrl(path)
}
