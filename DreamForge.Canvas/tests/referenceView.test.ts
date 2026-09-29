import { describe, expect, it } from 'vitest'
import { hasBlockedReference, isLockedVersionMissing, referenceVersionLabel } from '../src/ReferenceView'
import type { NodeReferenceThumb } from '../src/Protocol/VersionedMessages'

function thumb(patch: Partial<NodeReferenceThumb>): NodeReferenceThumb {
  return { entityId: 'e1', name: '沈砚', kind: 'Character', ...patch }
}

describe('引用版本语义跨端一致（目标 4）', () => {
  it('未锁定时显示「跟随最新」', () => {
    const reference = thumb({ variantId: 'v1', variantVersionId: undefined })
    expect(isLockedVersionMissing(reference)).toBe(false)
    expect(referenceVersionLabel(reference)).toBe('跟随最新')
  })

  it('锁定存在的版本时显示版本号', () => {
    const reference = thumb({
      variantId: 'v1',
      variantVersionId: 'ver-2',
      versions: [{ id: 'ver-1', label: 'v1', number: 1 }, { id: 'ver-2', label: 'v2', number: 2 }]
    })
    expect(isLockedVersionMissing(reference)).toBe(false)
    expect(referenceVersionLabel(reference)).toBe('v2')
  })

  it('锁定版本缺失时判定为阻断，而不是静默跟随最新', () => {
    const reference = thumb({
      variantId: 'v1',
      variantVersionId: 'ver-removed',
      versions: [{ id: 'ver-1', label: 'v1', number: 1 }]
    })
    expect(isLockedVersionMissing(reference)).toBe(true)
    expect(referenceVersionLabel(reference)).toBe('版本缺失（阻断）')
  })

  it('版本列表整段缺失时也视为版本缺失', () => {
    const reference = thumb({ variantId: 'v1', variantVersionId: 'ver-2' })
    expect(isLockedVersionMissing(reference)).toBe(true)
  })

  it('节点上只要有阻断引用就无法视为正常出图', () => {
    const blocked = thumb({ variantId: 'v1', variantVersionId: 'gone', versions: [] })
    const ok = thumb({ variantId: 'v1', variantVersionId: undefined })
    expect(hasBlockedReference([ok])).toBe(false)
    expect(hasBlockedReference([ok, blocked])).toBe(true)
  })
})
