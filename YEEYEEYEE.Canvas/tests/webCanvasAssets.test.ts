import { describe, expect, it } from 'vitest'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { mapAssets, PresenceChip, recordAttachments, recordReferences, resolveReference } from '../src/WebCanvasApp'
import { CanvasView } from '../src/shell/CanvasView'
import type { ViewRecord } from '../src/shell/records'
import type { PresencePerson } from '../src/shell/serverEvents'

const person = (userId: string, basis: string, connections: number): PresencePerson => ({
  userId, displayName: userId, basis, connections, clients: ['web'], lastSeenSeconds: 10
})

describe('在线展示', () => {
  it('仅活连接计入在线人数和提示名单，recent 明确显示最近活跃', () => {
    const html = renderToStaticMarkup(createElement(PresenceChip, { people: [
      person('在线用户', 'connection', 2), person('刚断线', 'recent', 0),
      person('零连接', 'connection', 0), person('未知依据', 'unknown', 3), person('负连接', 'connection', -1)
    ] }))
    expect(html).toContain('1 人在线')
    expect(html).toContain('title="1 人在线：在线用户"')
    expect(html).toContain('刚断线（最近活跃）')
    expect(html).toContain('零连接（未确认在线）')
    expect(html).toContain('未知依据（未确认在线）')
    expect(html).toContain('负连接（未确认在线）')
  })

  it('仅最近活跃时仍能展开查看但显示 0 人在线，空名单不显示', () => {
    const html = renderToStaticMarkup(createElement(PresenceChip, { people: [person('刚断线', 'recent', 0)] }))
    expect(html).toContain('0 人在线')
    expect(html).toContain('刚断线（最近活跃）')
    expect(renderToStaticMarkup(createElement(PresenceChip, { people: [] }))).toBe('')
  })
})

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

  it('reads node record.attachments (metadata only) and skips the ones with no id', () => {
    const list = recordAttachments({ recordId: 'node', recordType: 'Shot', record: { attachments: [
      { id: 'a1', kind: 'Image', name: '第一张', source: '出图', addedAt: '2026-10-03T00:00:00Z' },
      { id: 'a2', kind: 'Video', name: '' },
      { id: 'a3' },
      { kind: 'Image', name: '没有 id' },
      'nonsense'
    ] } })
    // 身份是 id：只有它缺了才算读不懂（与 recordReferences 只认 entityId 同一个态度）。
    expect(list.map((item) => item.id)).toEqual(['a1', 'a2', 'a3'])
    expect(list[0]).toMatchObject({ kind: 'Image', name: '第一张', source: '出图' })
    // 没名字的给一个可读的占位，不是空字符串——空名字在列表里就是一行空白。
    expect(list[1]).toMatchObject({ kind: 'Video', name: '未命名产物', source: '' })
    // 没 kind 的算「其他」：认不出来也别把它整条丢掉。
    expect(list[2]).toMatchObject({ kind: 'Other', name: '未命名产物' })
  })

  it('retains entity editing and version metadata without exposing media references', () => {
    const mapped = mapAssets({ entities: [{ id: 'e', name: '角色', core: '核心', aliases: '别名', variants: [{
      id: 'v', description: '描述', currentVersion: 2, hasUncommittedChanges: true,
      attachments: [{ id: 'a', name: '媒体', kind: 'Image', reference: 'private-path' }],
      versions: [{ id: 'history', number: 2, description: '历史描述', note: '备注',
        // 实体库下发的 `kind` 是数字（AttachmentKind），历史版本的媒体也一样。
        attachments: [{ id: 'h1', name: '历史图', kind: 0, reference: '/api/web/entities/e/variants/v/media/h1?versionId=history' },
          { id: 'h2', name: '历史视频', kind: 1 }] }]
    }] }] }).entities[0]
    expect(mapped).toMatchObject({ core: '核心', aliases: '别名', variants: [{
      description: '描述', currentVersion: 2, hasUncommittedChanges: true,
      versions: [{ description: '历史描述', note: '备注' }]
    }] })
    expect(mapped.variants?.[0].attachments?.[0]).not.toHaveProperty('reference')
    // 每个历史版本自己的媒体也要读得出来（锁定版本的引用就是照它取字节的），
    // 且数字种类要归一成 Image / Video——否则界面上会一律显示「不能预览」。
    expect(mapped.variants?.[0].versions?.[0].attachments).toEqual([
      { id: 'h1', name: '历史图', kind: 'Image', source: '' },
      { id: 'h2', name: '历史视频', kind: 'Video', source: '' }
    ])
  })

  it('treats a missing attachments field as none (most nodes have no products)', () => {
    expect(recordAttachments({ recordId: 'node', recordType: 'Shot', record: {} })).toEqual([])
    expect(recordAttachments({ recordId: 'node', recordType: 'Shot', record: { attachments: null } })).toEqual([])
  })
})

/**
 * 画布卡片上的引用徽标与检查器里的引用详情，必须给出同一个结论。
 *
 * 两边读的是同一份投影，但解析参数曾经不一致：资产库里没有这条实体时，
 * 检查器能用场景投影兜底（投影里带着 name / variantLabel / versions），卡片却报「实体缺失」——
 * 同一条引用，点开是对的、卡片上是红的。
 */
describe('画布卡片的引用徽标', () => {
  const NODE = '33333333-3333-4333-8333-333333333333'
  const nodeWith = (reference: Record<string, unknown>): ViewRecord => ({
    recordId: NODE, recordType: 'storyboard',
    record: { title: '镜头', content: '', x: 0, y: 0, references: [reference] }
  })
  const badge = (reference: Record<string, unknown>) => renderToStaticMarkup(createElement(CanvasView, {
    records: [nodeWith(reference)], selectedId: '', activeChapter: 'all',
    assets: [], assetsReady: true, showReferencePreviews: true,
    zoom: 1, pan: { x: 0, y: 0 }, onZoom: () => {}, onPan: () => {}, onSelect: () => {}
  }))

  it('资产库里没有这一条、但场景投影带着资料：照样显示它，不报「实体缺失」', () => {
    const projection = { entityId: 'gone', name: '同名角色', variantId: 'v1', variantLabel: '默认', versions: [{ id: 'ver1', number: 1 }] }
    // 检查器用的那条路（带场景投影兜底）能解析出来——卡片必须一致。
    expect(resolveReference(projection, [], true, true).error).toBeUndefined()
    const markup = badge(projection)
    expect(markup).toContain('同名角色')
    expect(markup).not.toContain('is-missing')
    expect(markup).not.toContain('实体缺失')
  })

  it('投影里明确说这条引用没解析出来（unresolved）：仍然如实报缺失，不假装解析成功', () => {
    const markup = badge({ entityId: 'gone', name: '同名角色', variantId: 'v1', variantLabel: '默认', versions: [{ id: 'ver1' }], unresolved: true })
    expect(markup).toContain('is-missing')
    expect(markup).toContain('实体缺失')
  })
})
