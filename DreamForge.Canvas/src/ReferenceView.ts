import type { NodeReferenceThumb, NodeReferenceVersion } from './Protocol/VersionedMessages'

/**
 * 引用版本语义（目标 4 / 4.2）：跨端一致地判断「跟随最新 / 锁定版本 / 版本缺失」。
 *
 * 与桌面端 `CanvasReferenceVersions` 同一口径：锁定了版本却找不到该版本时，
 * 不能当成「跟随最新」继续用，必须暴露为阻断状态。
 */

function versionsOf(reference: NodeReferenceThumb): NodeReferenceVersion[] {
  return Array.isArray(reference.versions) ? reference.versions : []
}

/** 该引用锁定的版本是否已缺失（锁定了一个不在变体版本列表里的版本）。 */
export function isLockedVersionMissing(reference: NodeReferenceThumb): boolean {
  if (!reference.variantVersionId) return false
  return !versionsOf(reference).some((version) => version.id === reference.variantVersionId)
}

/** 版本显示文本：跟随最新 / 版本号 / 版本缺失（阻断）。 */
export function referenceVersionLabel(reference: NodeReferenceThumb): string {
  if (!reference.variantVersionId) return '跟随最新'
  if (isLockedVersionMissing(reference)) return '版本缺失（阻断）'
  const match = versionsOf(reference).find((version) => version.id === reference.variantVersionId)
  return match?.label ?? reference.variantLabel ?? '锁定版本'
}

/** 该节点的引用里是否存在版本缺失的阻断项。 */
export function hasBlockedReference(references: NodeReferenceThumb[]): boolean {
  return references.some(isLockedVersionMissing)
}
