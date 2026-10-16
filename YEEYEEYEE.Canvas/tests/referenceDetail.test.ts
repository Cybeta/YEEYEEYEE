import { describe, expect, it } from 'vitest'
import { mapAssets, type Asset, type Reference } from '../src/assets'
import { referenceDetail, referenceKey } from '../src/shell/referenceDetail'

/**
 * 引用详情：一条引用该显示哪些资料、以及**这一版实际解析到的媒体**。
 * 最要紧的两条：锁定版本必须读版本自己的媒体；跟随最新读变体的当前附件。
 */
const assets: Asset[] = mapAssets({ entities: [{
  id: 'e1', name: '林夏', kind: 'Character', core: '核心设定', aliases: '小夏;夏',
  variants: [{
    id: 'v1', name: '默认', description: '当前变体描述',
    attachments: [{ id: 'cur-image', kind: 'Image', name: '当前图' }, { id: 'cur-video', kind: 'Video', name: '当前视频' }],
    versions: [
      { id: 'ver2', number: 2, label: 'v2', note: '第二版', description: '第二版描述',
        attachments: [{ id: 'v2-image', kind: 'Image', name: '第二版图' }, { id: 'v2-video', kind: 'Video', name: '第二版视频' }] },
      { id: 'ver1', number: 1, label: 'v1', attachments: [] },
      { id: 'ver3', number: 3, label: 'v3', attachments: [{ id: 'v3-image', kind: 'Image', name: '第三版图' }] }
    ]
  }]
}] }).entities

const follow: Reference = { entityId: 'e1', name: '林夏', kind: 'Character', variantId: 'v1', variantVersionId: null, variantLabel: '默认' }

describe('引用详情：跟随最新与锁定版本分别取哪一份媒体', () => {
  it('跟随最新：用变体当前的附件，URL 不带版本号', () => {
    const detail = referenceDetail(follow, assets, true)
    expect(detail.mode).toBe('跟随最新')
    expect(detail.versionLabel).toBe('最新')
    expect(detail.mediaSource).toBe('variant')
    expect(detail.media.map((item) => item.id)).toEqual(['cur-image', 'cur-video'])
    expect(detail.media[0].url).toBe('/api/web/entities/e1/variants/v1/media/cur-image')
    expect(detail.state).toBe('ok')
  })

  it('锁定版本：用**那个版本自己**的附件，URL 带上 versionId', () => {
    const detail = referenceDetail({ ...follow, variantVersionId: 'ver2' }, assets, true)
    expect(detail.mode).toBe('锁定版本')
    expect(detail.versionLabel).toBe('v2')
    expect(detail.mediaSource).toBe('version')
    expect(detail.media.map((item) => item.id)).toEqual(['v2-image', 'v2-video'])
    expect(detail.media[0].url).toBe('/api/web/entities/e1/variants/v1/media/v2-image?versionId=ver2')
  })

  it('锁定版本自己没有媒体时**不退回**当前变体的最新附件', () => {
    const detail = referenceDetail({ ...follow, variantVersionId: 'ver1' }, assets, true)
    expect(detail.media).toEqual([])
    expect(detail.mediaSource).toBe('version')
    expect(detail.note).toContain('没有记下媒体')
    // 变体现有两条附件，但一条都不能出现在锁定版本里。
    expect(detail.note).not.toContain('当前变体')
  })

  it('版本描述优先取该版本自己的；变体描述在跟随最新时给出', () => {
    expect(referenceDetail({ ...follow, variantVersionId: 'ver2' }, assets, true).description).toBe('第二版描述')
    expect(referenceDetail(follow, assets, true).description).toBe('当前变体描述')
  })

  it('实体资料（名称 / 类型 / 别名 / 核心）来自解析出来的实体', () => {
    const detail = referenceDetail(follow, assets, true)
    expect(detail).toMatchObject({ entityName: '林夏', entityKind: 'Character', aliases: '小夏;夏', core: '核心设定' })
  })
})

describe('引用详情：解析不出来时如实给原因', () => {
  it('实体缺失 / 变体缺失 / 锁定版本缺失都是 broken，并带原因', () => {
    const missingEntity = referenceDetail({ ...follow, entityId: 'gone' }, assets, true)
    expect(missingEntity.state).toBe('broken')
    expect(missingEntity.error).toContain('实体缺失')
    expect(missingEntity.entityName).toBe('林夏')

    const missingVariant = referenceDetail({ ...follow, variantId: 'gone' }, assets, true)
    expect(missingVariant.state).toBe('broken')
    expect(missingVariant.error).toContain('变体缺失')

    const missingVersion = referenceDetail({ ...follow, variantVersionId: 'gone' }, assets, true)
    expect(missingVersion.state).toBe('broken')
    expect(missingVersion.error).toContain('锁定版本缺失')
    expect(missingVersion.media).toEqual([])
    expect(missingVersion.mediaSource).toBe('none')
  })

  it('资源库不可用时说明是资源库的问题，而不是「这条引用没了」', () => {
    const detail = referenceDetail(follow, [], false)
    expect(detail.state).toBe('broken')
    expect(detail.error).toContain('资产库不可用')
  })

  it('认不出实体时退回投影里的名字与类型，不显示空白', () => {
    const detail = referenceDetail({ entityId: 'gone', name: '投影里的名字', kind: 'Scene', variantId: 'v1' }, assets, true)
    expect(detail.entityName).toBe('投影里的名字')
    expect(detail.entityKind).toBe('Scene')
  })
})

describe('引用身份键', () => {
  it('锁定版本与跟随最新不是同一条；同一条引用稳定', () => {
    expect(referenceKey(follow)).toBe('e1|v1|latest')
    expect(referenceKey({ ...follow, variantVersionId: 'ver2' })).toBe('e1|v1|ver2')
    expect(referenceKey({ ...follow })).toBe(referenceKey(follow))
  })
})
