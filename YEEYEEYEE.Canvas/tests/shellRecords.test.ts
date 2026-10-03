import { describe, expect, it } from 'vitest'
import { clampZoom, wheelNotches, wheelZoomFactor, zoomAround } from '../src/shell/CanvasView'
import {
  PLANNING_GROUP_ID, UNFILED_GROUP_ID, canvasBounds, chapterGroups, isEditableRecord, isPlanning,
  kindOf, NODE_KINDS, parseScene, scriptEntries, stageSummaries, type ViewRecord
} from '../src/shell/records'

/**
 * 工作台那批纯规则。
 *
 * 这里钉的都是「错了就会静默显示一张错画布」的地方：响应校验、节点身份、章节分组、阶段计数。
 * 它们全是纯函数，所以能直接测——搬到组件里就只能靠手点，而手点发现不了「同名章节被合并」
 * 或者「工作树章节被算了两遍」这类问题。
 */

const GUID_A = '11111111-1111-4111-8111-111111111111'
const GUID_B = '22222222-2222-4222-8222-222222222222'
const GUID_C = '33333333-3333-4333-8333-333333333333'
const GUID_D = '44444444-4444-4444-8444-444444444444'

function node(recordId: string, recordType: string, record: Record<string, unknown> = {}): ViewRecord {
  return { recordId, recordType, record }
}

describe('parseScene 校验服务端响应', () => {
  it('收下项目模式的完整响应，并把项目名与画布名读出来', () => {
    const scene = parseScene({
      revision: 405,
      records: [node(GUID_A, 'storyboard', { title: '分镜' })],
      readOnly: true,
      formatVersion: 2,
      projectName: '雾港来信',
      canvasTitle: '第一章 · 雨夜码头',
      migration: { changed: false },
      validation: { hasErrors: false }
    })
    expect(scene).toMatchObject({ revision: 405, readOnly: true, formatVersion: 2, projectName: '雾港来信', canvasTitle: '第一章 · 雨夜码头' })
    expect(scene.records).toHaveLength(1)
  })

  it('独立场景模式没有 readOnly / 项目名，缺省成 false 与 undefined 而不是猜一个值', () => {
    const scene = parseScene({ revision: 0, records: [] })
    expect(scene.readOnly).toBe(false)
    expect(scene.projectName).toBeUndefined()
    expect(scene.canvasTitle).toBeUndefined()
    expect(scene.formatVersion).toBeUndefined()
  })

  it('空白名字当作没有名字，不显示一个空格子', () => {
    const scene = parseScene({ revision: 1, records: [], projectName: '   ', canvasTitle: '' })
    expect(scene.projectName).toBeUndefined()
    expect(scene.canvasTitle).toBeUndefined()
  })

  it('形状不对就抛，不返回一个「看起来正常的空画布」', () => {
    expect(() => parseScene(null)).toThrow('响应格式不正确')
    expect(() => parseScene([])).toThrow('响应格式不正确')
    expect(() => parseScene({ records: [] })).toThrow('响应格式不正确')
    expect(() => parseScene({ revision: -1, records: [] })).toThrow('响应格式不正确')
    expect(() => parseScene({ revision: 1, records: {} })).toThrow('响应格式不正确')
    expect(() => parseScene({ revision: 1, records: [{ recordId: '', recordType: 'x', record: {} }] })).toThrow('响应格式不正确')
    expect(() => parseScene({ revision: 1, records: [{ recordId: 'a', recordType: 'x', record: null }] })).toThrow('响应格式不正确')
    expect(() => parseScene({ revision: 1, records: [{ recordId: 'a', recordType: 'x', record: [] }] })).toThrow('响应格式不正确')
  })
})

describe('节点身份与种类', () => {
  it('只有稳定 GUID 的记录是画布节点，工作树章节行不是', () => {
    expect(isEditableRecord(node(GUID_A, 'chapter', {}))).toBe(true)
    expect(isEditableRecord(node(`wt-${GUID_A}`, 'chapter', {}))).toBe(false)
  })

  it('九个种类各有各的颜色，映射跟着 NodeProjection 的取值走', () => {
    expect(kindOf('story-plan')).toBe('plan')
    expect(kindOf('story-outline')).toBe('outline')
    expect(kindOf('chapter')).toBe('chapter')
    expect(kindOf('storyboard')).toBe('storyboard')
    expect(kindOf('product')).toBe('product')
    expect(kindOf('character')).toBe('character')
    expect(kindOf('scene-description')).toBe('scene')
    expect(kindOf('prop')).toBe('prop')
    expect(kindOf('general')).toBe('general')
    expect(kindOf('完全没见过的类型')).toBe('general')
    const hexes = Object.values(NODE_KINDS).map((kind) => kind.hex)
    expect(new Set(hexes).size).toBe(hexes.length)
  })
})

describe('章节分组', () => {
  const records: ViewRecord[] = [
    node(GUID_A, 'chapter', { title: '第一章', chapterId: 'ch-1', order: 0 }),
    node(`wt-ch-2`, 'chapter', { title: '第二章', workTreeItemId: 'ch-2', order: 1 }),
    node(GUID_B, 'story-plan', { title: '总企划' }),
    node(GUID_C, 'storyboard', { title: '分镜 A', chapterId: 'ch-1', x: 100, y: 50 }),
    node(GUID_D, 'character', { title: '林晚', chapterId: 'ch-1', x: 100, y: 20 })
  ]

  it('章节定义不算泳道里的节点，它自己的文字挂在分组上', () => {
    const groups = chapterGroups(records)
    expect(groups.map((group) => group.label)).toEqual(['企划', '第一章', '第二章'])
    const first = groups.find((group) => group.label === '第一章')
    expect(first?.records.map((item) => item.recordId)).toEqual([GUID_D, GUID_C])
    expect(first?.body).toBe('')
  })

  it('企划层不进章节泳道，单独排在最前', () => {
    const groups = chapterGroups(records)
    expect(groups[0].id).toBe(PLANNING_GROUP_ID)
    expect(groups[0].records.map((item) => item.recordId)).toEqual([GUID_B])
  })

  it('认不出来的类型（layer 0）不算企划层——否则角色与道具会被永远留在画面上', () => {
    expect(isPlanning(node(GUID_A, 'story-plan', {}))).toBe(true)
    expect(isPlanning(node(GUID_B, 'story-outline', {}))).toBe(true)
    expect(isPlanning(node(GUID_C, 'character', {}))).toBe(false)
    expect(isPlanning(node(GUID_C, 'scene-description', {}))).toBe(false)
    expect(isPlanning(node(GUID_C, 'prop', {}))).toBe(false)
    expect(isPlanning(node(GUID_C, 'general', {}))).toBe(false)
    expect(isPlanning(node(GUID_C, '完全没见过的类型', {}))).toBe(false)
    // 角色挂在章节里，就不该出现在企划组里
    expect(chapterGroups(records)[0].records.map((item) => item.record.id)).not.toContain('林晚')
  })

  it('没有节点的章节照样出现在时间轴上，只是空的', () => {
    const groups = chapterGroups(records)
    expect(groups.find((group) => group.label === '第二章')?.records).toEqual([])
  })

  it('没挂在任何已知章节上的节点进「未归档」，不被静默丢掉', () => {
    const withOrphan = [...records, node(GUID_D.replace('4', '5'), 'prop', { title: '无人认领的道具' })]
    const groups = chapterGroups(withOrphan)
    const unfiled = groups[groups.length - 1]
    expect(unfiled.id).toBe(UNFILED_GROUP_ID)
    expect(unfiled.label).toBe('未归档')
    expect(unfiled.records.map((item) => item.record.title)).toEqual(['无人认领的道具'])
  })

  it('指向一个已不存在的章节的节点也进「未归档」', () => {
    const dangling = node(GUID_D.replace('4', '6'), 'prop', { title: '悬空引用', chapterId: 'ch-gone' })
    const groups = chapterGroups([...records, dangling])
    expect(groups[groups.length - 1].records.map((item) => item.record.title)).toEqual(['悬空引用'])
  })
})

describe('剧本与阶段统计', () => {
  const records: ViewRecord[] = [
    node(GUID_A, 'chapter', { title: '第一章', chapterId: 'ch-1', order: 0 }),
    node(`wt-ch-1`, 'chapter', { title: '第一章', workTreeItemId: 'ch-1', order: 0 }),
    node(GUID_C, 'storyboard', { title: '分镜 A', content: '雨夜码头', chapterId: 'ch-1', order: 2 }),
    node(GUID_D, 'storyboard', { title: '分镜 B', content: '末班公交', chapterId: 'ch-1', order: 1 }),
    node(GUID_B, 'product', { title: '成片', content: '第一集成片' })
  ]

  it('剧本按「章节标题 + 章内顺序」摊开，顺序用工作树显式顺序而不是坐标', () => {
    const entries = scriptEntries(records)
    const chapterIndex = entries.findIndex((entry) => entry.kind === 'chapter' && entry.title === '第一章')
    expect(chapterIndex).toBeGreaterThanOrEqual(0)
    expect(entries[chapterIndex + 1]).toMatchObject({ kind: 'node', title: '分镜 B', body: '末班公交' })
    expect(entries[chapterIndex + 2]).toMatchObject({ kind: 'node', title: '分镜 A', body: '雨夜码头' })
  })

  it('阶段计数只数真实节点，工作树章节行不被算第二遍', () => {
    const summaries = stageSummaries(records)
    expect(summaries.map((item) => [item.label, item.count])).toEqual([['企划', 0], ['章节', 1], ['分镜', 2], ['成品', 1]])
  })

  it('世界画布装得下所有节点，且不小于桌面端那张', () => {
    expect(canvasBounds([])).toEqual({ width: 4200, height: 3000 })
    expect(canvasBounds([node(GUID_A, 'general', { x: 5000, y: 4000 })])).toEqual({ width: 5600, height: 4400 })
  })
})

describe('缩放换算', () => {
  it('缩放比例被夹在与桌面端相同的 25%–220% 之间', () => {
    expect(clampZoom(0.01)).toBe(0.25)
    expect(clampZoom(9)).toBe(2.2)
    expect(clampZoom(0.86)).toBe(0.86)
  })

  it('滚轮一格约 1.1275 倍，方向与桌面端一致（向上滚放大）', () => {
    // 浏览器向上滚 deltaY 为负 → 要放大；桌面端的曲线是一格 exp(0.12)。
    expect(wheelZoomFactor(-100, 0)).toBeCloseTo(Math.exp(0.12), 9)
    expect(wheelZoomFactor(-100, 0)).toBeGreaterThan(1)
    expect(wheelZoomFactor(100, 0)).toBeLessThan(1)
    // 两个方向互为倒数：连滚上去再滚下来应当回到原处
    expect(wheelZoomFactor(-100, 0) * wheelZoomFactor(100, 0)).toBeCloseTo(1, 9)
  })

  it('像素 / 行 / 页三种 deltaMode 归一到同一格，倍率才不会被浏览器换掉', () => {
    expect(wheelNotches(-100, 0)).toBeCloseTo(-1, 9)
    expect(wheelNotches(-3, 1)).toBeCloseTo(-1, 9)
    expect(wheelNotches(-1, 2)).toBeCloseTo(-1, 9)
    expect(wheelZoomFactor(-3, 1)).toBeCloseTo(wheelZoomFactor(-100, 0), 9)
    expect(wheelZoomFactor(-1, 2)).toBeCloseTo(wheelZoomFactor(-100, 0), 9)
  })

  it('缩放时光标下的那个世界坐标点停在原地', () => {
    // 平移 (100,50)、缩放 1，光标在 (300,250)：那一点的世界坐标是 (200,200)。
    const next = zoomAround({ x: 100, y: 50 }, 1, 2, { x: 300, y: 250 })
    // 缩放翻倍后它应该还是落在 (300,250)：200*2 + next.x = 300
    expect(next).toEqual({ x: -100, y: -150 })
    expect(next.x + 200 * 2).toBe(300)
    expect(next.y + 200 * 2).toBe(250)
  })
})
