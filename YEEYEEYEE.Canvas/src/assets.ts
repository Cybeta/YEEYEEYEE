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
