import { describe, expect, it } from 'vitest'
import { mapAssets, recordReferences, resolveReference } from '../src/WebCanvasApp'

describe('WebCanvas assets from project entities.json', () => {
  const { entities } = mapAssets({ entities: [
    { id: 'entity-a', name: '同名', kind: 'Character', variants: [{ id: 'variant-a', name: '默认', versions: [{ id: 'version-a', number: 2 }] }] },
    { id: 'entity-b', name: '同名', kind: 'Scene', variants: [{ id: 'variant-b', versions: [{ id: 'version-b', label: 'v1' }] }] },
    { entityId: 'legacy', name: '错误键' }
  ] })

  it('maps id rather than entityId, keeps same-name identities separate and retains nested stable IDs', () => {
    expect(entities.map((asset) => asset.id)).toEqual(['entity-a', 'entity-b'])
    expect(entities[0].variants?.[0].versions?.[0].id).toBe('version-a')
    expect(entities[1].variants?.[0].id).toBe('variant-b')
    expect(mapAssets({ entities: [{ entityId: 'legacy', name: '错误键' }] }).invalidCount).toBe(1)
    expect(() => mapAssets({ entityId: 'wrong' })).toThrow('响应格式不正确')
  })

  it('resolves node record.references by entity ID, including lock and follow modes', () => {
    const refs = recordReferences({ recordId: 'node', recordType: 'Shot', record: { references: [
      { entityId: 'entity-b', name: '同名', variantId: 'variant-b', variantVersionId: 'version-b' },
      { entityId: 'entity-a', name: '同名', variantId: 'variant-a', variantVersionId: null },
      { name: 'invalid' }
    ] } })
    expect(refs).toHaveLength(2)
    expect(resolveReference(refs[0], entities)).toMatchObject({ asset: { id: 'entity-b' }, variant: { id: 'variant-b' }, version: { id: 'version-b' }, mode: '锁定版本' })
    expect(resolveReference(refs[1], entities)).toMatchObject({ asset: { id: 'entity-a' }, mode: '跟随最新' })
  })

  it('reports missing entity, variant, locked version and unavailable library without pretending success', () => {
    expect(resolveReference({ entityId: 'gone', variantId: 'v' }, entities).error).toContain('实体缺失')
    expect(resolveReference({ entityId: 'entity-a', variantId: 'gone' }, entities).error).toContain('变体缺失')
    expect(resolveReference({ entityId: 'entity-a', variantId: 'variant-a', variantVersionId: 'gone' }, entities)).toMatchObject({ mode: '锁定版本', error: '锁定版本缺失：gone' })
    expect(resolveReference({ entityId: 'entity-a', variantId: 'variant-a' }, [], false).error).toContain('资产库不可用')
  })
})
