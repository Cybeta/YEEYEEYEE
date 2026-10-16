import { resolveReference, type Asset, type AttachmentSummary, type Reference } from '../assets'
import { entityMediaUrl } from './assetImages'

/**
 * 一条引用的「详情」该显示什么——实体、变体、版本，以及**这一版实际解析到的媒体**。
 *
 * 为什么要单独一层：媒体取哪一份不是看「这条引用挂在哪个变体上」，而是看它**锁没锁版本**。
 * 锁定版本时那份媒体在那个历史版本自己的附件里（服务端按 `versionId` 找），
 * 退回当前变体的最新附件就等于展示了一件引用里根本没有的东西。
 * 所以解析结果与媒体必须来自同一次解析（`resolveReference`），这里只做这一步，不做第二套查找。
 *
 * 纯读取：不碰 React、不发请求；坏的那条如实回原因，不编。
 */

export type ReferenceMode = '锁定版本' | '跟随最新'

/** 详情里的一条可看媒体。`url` 是服务端已鉴权的那条字节接口（带版本号）。 */
export type ReferenceMedia = AttachmentSummary & { url: string | null }

export type ReferenceDetail = {
  /** 稳定身份：同一条引用在同一次渲染里算出来的键（展开状态、失败状态都按它隔离）。 */
  key: string
  entityId: string
  entityName: string
  entityKind: string
  description: string
  aliases: string
  core: string
  variantName: string
  versionLabel: string
  mode: ReferenceMode
  /** `ok` 解析得出来；`broken` 有明确原因（缺实体 / 缺变体 / 缺锁定版本 / 资源库不可用）。 */
  state: 'ok' | 'broken'
  error: string
  /** 媒体来自哪一层：锁定版本读版本自己的，跟随最新读变体的当前附件。 */
  mediaSource: 'version' | 'variant' | 'none'
  media: ReferenceMedia[]
  /** 这一版**没有任何媒体**时的说明（不是失败，也不是「加载中」）。 */
  note: string
}

export function referenceKey(reference: Pick<Reference, 'entityId' | 'variantId' | 'variantVersionId'>): string {
  return `${reference.entityId}|${reference.variantId ?? ''}|${reference.variantVersionId ?? 'latest'}`
}

export function referenceDetail(reference: Reference, assets: Asset[], assetsReady: boolean): ReferenceDetail {
  const resolved = resolveReference(reference, assets, assetsReady, true)
  const variant = resolved.variant
  const version = resolved.version
  const locked = !!reference.variantVersionId
  const source: ReferenceDetail['mediaSource'] = locked ? (version ? 'version' : 'none') : variant ? 'variant' : 'none'
  const raw = locked ? version?.attachments ?? [] : variant?.attachments ?? []
  const media: ReferenceMedia[] = raw.map((attachment) => ({
    ...attachment,
    // 没有变体 ID 就拼不出受鉴权的那条接口：如实给 null，由界面说「不可读取」，不去猜一条假 URL。
    url: reference.entityId && reference.variantId
      ? entityMediaUrl(reference.entityId, reference.variantId, attachment.id, locked ? reference.variantVersionId : null)
      : null
  }))
  return {
    key: referenceKey(reference),
    entityId: reference.entityId,
    entityName: resolved.asset?.name || reference.name || '未知实体',
    entityKind: resolved.asset?.kind || reference.kind || '',
    // 描述与媒体取自**同一次解析**：锁定版本时资料也来自那个版本（与 ReferenceContent 一致）。
    description: (locked ? version?.description : variant?.description)?.trim() ?? '',
    aliases: resolved.asset?.aliases?.trim() ?? '',
    core: resolved.asset?.core?.trim() ?? '',
    variantName: variant?.name || reference.variantLabel || '',
    versionLabel: version ? (version.label || (version.number != null ? `v${version.number}` : version.id)) : (locked ? '版本缺失' : '最新'),
    mode: locked ? '锁定版本' : '跟随最新',
    state: resolved.error ? 'broken' : 'ok',
    error: resolved.error ?? '',
    mediaSource: source,
    media,
    note: media.length > 0 ? '' : locked
      ? (version ? '这个历史版本没有记下媒体' : '锁定版本缺失，读不到它的媒体')
      : (variant ? '这条变体还没有媒体' : '')
  }
}
