import { describe, expect, it } from 'vitest'
import { nodeConnections, parseScene, visibleEdges, type ShellEdge, type ViewRecord } from '../src/shell/records'

/**
 * 连线（edges）的读取口径。
 *
 * 这一段的要点是**容错的方向**：连线坏一条就跳过那一条，不炸整份场景。
 * 画布与连线是两层信息，为一条读不懂的连线让整张画布打不开，是拿次要的东西换主要的东西。
 * （记录本身仍然严格：一条记录读不懂就整份拒绝——那是主要的东西。）
 */

describe('场景里的连线', () => {
  it('正常解析：edgeId / sourceId / targetId', () => {
    const scene = parseScene({
      revision: 3,
      records: [],
      edges: [{ edgeId: 'e1', sourceId: 'a', targetId: 'b' }]
    })
    expect(scene.edges).toEqual([{ edgeId: 'e1', sourceId: 'a', targetId: 'b' }])
  })

  it('坏的那一条跳过，好的那一条照旧', () => {
    const scene = parseScene({
      revision: 3,
      records: [],
      edges: [
        { edgeId: 'e1', sourceId: 'a', targetId: 'b' },
        { edgeId: '', sourceId: 'a', targetId: 'b' },
        { edgeId: 'e3', sourceId: 'a' },
        '不是对象',
        null
      ]
    })
    expect(scene.edges).toEqual([{ edgeId: 'e1', sourceId: 'a', targetId: 'b' }])
  })

  it('没有这个字段（独立场景模式的老接口）、或者它不是数组：都当空', () => {
    expect(parseScene({ revision: 3, records: [] }).edges).toEqual([])
    expect(parseScene({ revision: 3, records: [], edges: '不是数组' }).edges).toEqual([])
  })
})

const GUID_A = '11111111-1111-4111-8111-111111111111'
const GUID_B = '22222222-2222-4222-8222-222222222222'
const GUID_C = '33333333-3333-4333-8333-333333333333'

function node(recordId: string, recordType = 'storyboard'): ViewRecord {
  return { recordId, recordType, record: {} }
}

function edge(edgeId: string, sourceId: string, targetId: string): ShellEdge {
  return { edgeId, sourceId, targetId }
}

/**
 * 画得出来的连线。服务端已经保证了「两端都还在画布里」，但画布自己还会再筛一遍：
 * 阶段芯片与搜索会把节点从画面上摘掉，少了一端的线画不出来，也不该画。
 */
describe('画得出来的连线', () => {
  it('两端都在画面上：留下', () => {
    const edges = [edge('e1', GUID_A, GUID_B)]
    expect(visibleEdges(edges, [node(GUID_A), node(GUID_B)])).toEqual(edges)
  })

  it('有一端被阶段芯片 / 搜索筛掉了：丢掉，不画半根线', () => {
    const edges = [edge('e1', GUID_A, GUID_B), edge('e2', GUID_B, GUID_C)]
    expect(visibleEdges(edges, [node(GUID_A), node(GUID_B)])).toEqual([edge('e1', GUID_A, GUID_B)])
  })

  it('章节行（wt-… 没有稳定 GUID）不算节点，连着它的线也画不出来', () => {
    const edges = [edge('e1', GUID_A, 'wt-rain-night')]
    expect(visibleEdges(edges, [node(GUID_A), node('wt-rain-night', 'chapter')])).toEqual([])
  })

  it('没有连线、或者画布上什么都没有：都当空，不炸', () => {
    expect(visibleEdges([], [node(GUID_A)])).toEqual([])
    expect(visibleEdges([edge('e1', GUID_A, GUID_B)], [])).toEqual([])
  })
})

/**
 * 某个节点自己的连线（检查器用它列「这个节点连着谁」，并给每条一个「断开」）。
 * 说清方向是必须的：只列另一头的话，「我连向它」与「它连向我」在界面上长得一模一样。
 */
describe('一个节点自己的连线', () => {
  it('连出去的与连进来的分开说：方向 + 另一头是谁', () => {
    const edges = [edge('e1', GUID_A, GUID_B), edge('e2', GUID_C, GUID_A)]
    expect(nodeConnections(edges, GUID_A)).toEqual([
      { edgeId: 'e1', direction: 'out', otherId: GUID_B },
      { edgeId: 'e2', direction: 'in', otherId: GUID_C }
    ])
  })

  it('与这个节点无关的连线一条都不列', () => {
    expect(nodeConnections([edge('e1', GUID_B, GUID_C)], GUID_A)).toEqual([])
  })

  it('落在别处的连线不影响自己的那几条', () => {
    const edges = [edge('e1', GUID_A, GUID_B), edge('e2', GUID_B, GUID_C), edge('e3', GUID_C, GUID_A)]
    expect(nodeConnections(edges, GUID_B)).toEqual([
      { edgeId: 'e1', direction: 'in', otherId: GUID_A },
      { edgeId: 'e2', direction: 'out', otherId: GUID_C }
    ])
  })

  it('自环只算一条（服务端拒绝创建，但画布文件是多人共写的，读进来要当真）', () => {
    expect(nodeConnections([edge('e1', GUID_A, GUID_A)], GUID_A)).toEqual([
      { edgeId: 'e1', direction: 'out', otherId: GUID_A }
    ])
  })

  it('没有连线：空数组，不炸', () => {
    expect(nodeConnections([], GUID_A)).toEqual([])
  })
})
