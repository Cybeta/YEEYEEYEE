import { describe, expect, it } from 'vitest'
import { attachmentFileUrl, entityMediaUrl, entityThumbUrl, withRetry } from '../src/shell/assetImages'

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

/** 节点产物图（第 177 轮）：比设定缩略图多一层记录 ID，且**没有变体 / 版本**可选。 */
describe('节点产物图的 URL', () => {
  it('两条 ID 都进路径，各转义各的', () => {
    expect(attachmentFileUrl('n1', 'a1')).toBe('/api/web/records/n1/attachments/a1/file')
    expect(attachmentFileUrl('n/1', 'a 1')).toBe('/api/web/records/n%2F1/attachments/a%201/file')
  })
})

/**
 * 设定媒体的 URL：`versionId` 决定读的是**哪个版本**的字节。
 * 漏掉它不会报错——只会静默换成当前变体的最新附件，而锁定版本的引用要的恰恰不是那一份。
 */
describe('设定媒体的 URL', () => {
  it('跟随最新不带 versionId，锁定版本必须带上', () => {
    expect(entityMediaUrl('e1', 'v1', 'a1')).toBe('/api/web/entities/e1/variants/v1/media/a1')
    expect(entityMediaUrl('e1', 'v1', 'a1', null)).toBe('/api/web/entities/e1/variants/v1/media/a1')
    expect(entityMediaUrl('e1', 'v1', 'a1', 'v3')).toBe('/api/web/entities/e1/variants/v1/media/a1?versionId=v3')
  })

  it('三段 ID 与版本号各转义各的', () => {
    expect(entityMediaUrl('e/1', 'v 1', 'a/1', 'v 3')).toBe('/api/web/entities/e%2F1/variants/v%201/media/a%2F1?versionId=v%203')
  })
})

/**
 * 失败重试时的地址：**重试必须换 URL**，否则浏览器会把上一次的结果（含失败）直接拿出来，
 * 按下去等于没按。序号只是浏览器这一侧的缓存键，服务端不看它。
 */
describe('重试地址', () => {
  it('没重试过就原样返回，不在正常地址上多挂一截参数', () => {
    expect(withRetry('/api/web/records/n1/attachments/a1/file', 0)).toBe('/api/web/records/n1/attachments/a1/file')
  })

  it('重试过就换一个地址；原本带查询串时接在它后面', () => {
    expect(withRetry('/api/web/records/n1/attachments/a1/file', 1)).toBe('/api/web/records/n1/attachments/a1/file?retry=1')
    expect(withRetry('/api/web/records/n1/attachments/a1/file?canvas=c1', 2)).toBe('/api/web/records/n1/attachments/a1/file?canvas=c1&retry=2')
  })
})
