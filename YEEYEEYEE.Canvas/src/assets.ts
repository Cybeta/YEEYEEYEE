import type { OperationRecord } from './Protocol/VersionedMessages'

export type Version = { id: string; number?: number; label?: string }
export type Variant = { id: string; name?: string; versions?: Version[] }
export type Asset = { id: string; name: string; kind?: string; variants?: Variant[] }
export type Reference = { entityId: string; name?: string; kind?: string; variantId?: string; variantVersionId?: string | null }

function hasId(value: unknown): value is { id: string; [key: string]: unknown } {
  return !!value && typeof value === 'object' && !Array.isArray(value) && typeof (value as { id?: unknown }).id === 'string' && !!(value as { id: string }).id.trim()
}

/** entities.json uses id at every level; names are labels, never identity keys. */
export function mapAssets(value: unknown): { entities: Asset[]; invalidCount: number } {
  if (!value || typeof value !== 'object' || !Array.isArray((value as { entities?: unknown }).entities)) throw new Error('响应格式不正确')
  const raw = (value as { entities: unknown[] }).entities
  const entities = raw.filter((item): item is Asset => hasId(item) && typeof item.name === 'string').map((item) => ({
    id: item.id, name: item.name, kind: typeof item.kind === 'string' ? item.kind : undefined,
    variants: Array.isArray(item.variants) ? item.variants.filter(hasId).map((variant) => ({
      id: variant.id, name: typeof variant.name === 'string' ? variant.name : undefined,
      versions: Array.isArray(variant.versions) ? variant.versions.filter(hasId).map((version) => ({
        id: version.id, number: typeof version.number === 'number' ? version.number : undefined,
        label: typeof version.label === 'string' ? version.label : undefined
      })) : undefined
    })) : undefined
  }))
  return { entities, invalidCount: raw.length - entities.length }
}

export function recordReferences(item: OperationRecord): Reference[] {
  const raw = item.record.references
  if (!Array.isArray(raw)) return []
  return raw.filter((ref): ref is Reference => !!ref && typeof ref === 'object' && typeof (ref as { entityId?: unknown }).entityId === 'string' && !!(ref as { entityId: string }).entityId.trim())
}

/** 节点出过的一个产物（图 / 视频 / 音频）。**里面没有路径**：取字节要按 ID 走接口。 */
export type AttachmentSummary = { id: string; kind: string; name: string; source: string; addedAt?: string }

/**
 * 读节点的 `record.attachments`（服务端投影的产物元数据）。
 *
 * 与 `recordReferences` 同一个态度：坏的那条**跳过**，不让它把整份场景炸掉——
 * 一条产物读不懂，用户还有别的可看；整份场景打不开就什么都没有了。
 * 服务端**故意没投影** `reference`（那是服务端的路径 / 一段 data URL）与 `prompt`（动辄上千字，
 * 每份场景都要带上所有节点），所以这里也不认这两项。
 */
export function recordAttachments(item: OperationRecord): AttachmentSummary[] {
  const raw = item.record.attachments
  if (!Array.isArray(raw)) return []
  return raw.flatMap((entry): AttachmentSummary[] => {
    if (!entry || typeof entry !== 'object') return []
    const row = entry as Record<string, unknown>
    if (typeof row.id !== 'string' || !row.id.trim()) return []
    return [{
      id: row.id,
      kind: typeof row.kind === 'string' ? row.kind : 'Other',
      name: typeof row.name === 'string' && row.name.trim() ? row.name : '未命名产物',
      source: typeof row.source === 'string' ? row.source : '',
      addedAt: typeof row.addedAt === 'string' ? row.addedAt : undefined
    }]
  })
}

export function resolveReference(ref: Reference, assets: Asset[], available = true): { asset?: Asset; variant?: Variant; version?: Version; error?: string; mode: string } {
  const mode = ref.variantVersionId ? '锁定版本' : '跟随最新'
  if (!available) return { mode, error: '资产库不可用，无法核对引用' }
  const asset = assets.find((item) => item.id === ref.entityId)
  if (!asset) return { mode, error: `实体缺失：${ref.entityId}` }
  if (!ref.variantId) return { asset, mode, error: '引用未指定变体 ID' }
  const variant = asset.variants?.find((item) => item.id === ref.variantId)
  if (!variant) return { asset, variant, mode, error: `变体缺失：${ref.variantId}` }
  if (!ref.variantVersionId) return { asset, variant, mode }
  const version = variant.versions?.find((item) => item.id === ref.variantVersionId)
  return version ? { asset, variant, version, mode } : { asset, variant, mode, error: `锁定版本缺失：${ref.variantVersionId}` }
}

/** 挑一条设定挂到节点上时，下拉里的一行。`value` 是 `<实体Id>|<变体Id>`。 */
export type ReferenceOption = { value: string; label: string; entityId: string; variantId: string | null }

/**
 * 把项目库摊成「可以挂上去的条目」：每个实体 × 它的每个变体一行。
 *
 * 实体**没有变体**时也要给出一行（`variantId` 为 null）：那条请求缺 variantId 时服务端会落到
 * 这个实体的第一个变体，是合法的；而「这个实体在网页端根本挑不到」是投影缺了一块，不该等到挑的时候才发现。
 * 名称只用来显示（实体名 · 变体名），**身份始终是 ID**——两端一致：名字是标签，不是键。
 */
export function referenceOptions(assets: Asset[]): ReferenceOption[] {
  return assets.flatMap((asset): ReferenceOption[] => {
    const variants = asset.variants ?? []
    if (variants.length === 0) return [{ value: asset.id, label: asset.name, entityId: asset.id, variantId: null }]
    return variants.map((variant) => ({
      value: `${asset.id}|${variant.id}`,
      label: variant.name ? `${asset.name} · ${variant.name}` : asset.name,
      entityId: asset.id,
      variantId: variant.id
    }))
  })
}

/** 把下拉的 value 拆回两个 ID。拆不出实体 ID 就返回 null——那说明这个 value 不是 referenceOptions 生成的。 */
export function parseReferenceOption(value: string): { entityId: string; variantId: string | null } | null {
  const [entityId, variantId = ''] = value.split('|')
  return entityId ? { entityId, variantId: variantId || null } : null
}
