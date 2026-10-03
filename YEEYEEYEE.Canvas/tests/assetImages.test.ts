import { describe, expect, it } from 'vitest'
import { entityThumbUrl } from '../src/shell/assetImages'

/**
 * 引用徽标上那张缩略图的 URL。
 *
 * 拼错了不会报错，只会**静默显示不出图**——所以这条单独有测试。
 * 最要紧的一条是「空参数不要拼上去」：服务端的 `?variantId=`（空串）与不给这个参数
 * 在语义上不是一回事，前者会被当成「变体 ID 非法」。
 */
describe('设定缩略图的 URL', () => {
  it('只有实体 ID 时不带查询串', () => {
    expect(entityThumbUrl('e1')).toBe('/api/web/entities/e1/thumb')
    expect(entityThumbUrl('e1', '')).toBe('/api/web/entities/e1/thumb')
    expect(entityThumbUrl('e1', null, null)).toBe('/api/web/entities/e1/thumb')
  })

  it('给了变体 / 版本才带上；引用里的「没锁版本」（null）只说变体', () => {
    expect(entityThumbUrl('e1', 'v1')).toBe('/api/web/entities/e1/thumb?variantId=v1')
    expect(entityThumbUrl('e1', 'v1', null)).toBe('/api/web/entities/e1/thumb?variantId=v1')
    expect(entityThumbUrl('e1', 'v1', 'v3')).toBe('/api/web/entities/e1/thumb?variantId=v1&versionId=v3')
  })

  it('转义交给 URL 那一层，不靠「ID 恰好是 GUID」', () => {
    expect(entityThumbUrl('a/b', 'v 1')).toBe('/api/web/entities/a%2Fb/thumb?variantId=v+1')
  })
})
