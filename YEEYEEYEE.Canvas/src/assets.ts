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
