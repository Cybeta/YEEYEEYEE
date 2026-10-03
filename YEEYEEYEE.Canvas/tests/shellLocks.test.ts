import { describe, expect, it } from 'vitest'
import { parseLayoutPlan, ghostMoves, movingIds, scopeLabel } from '../src/shell/layoutPlan'
import { clientLabel, describeLease, holderOf, isMine, leaseCovering, nodeLease, parseLease, parseLeases, shouldHoldNodeLease, treeLease } from '../src/shell/locks'

/**
 * 编辑锁与整理计划的读取口径。
 *
 * 这两块错了都不会报错，只会**显示错**：锁错了会让人以为「没人编辑」于是放心去改，
 * 计划错了会把虚影画到别处。它们全是纯函数，所以能直接钉住。
 */

const NODE_A = '11111111-1111-4111-8111-111111111111'
const NODE_B = '22222222-2222-4222-8222-222222222222'

function lease(overrides: Record<string, unknown> = {}) {
  return {
    leaseId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    scope: 'node',
    targetId: NODE_A,
    userId: '99999999-9999-4999-8999-999999999999',
    displayName: '陈默',
    client: 'desktop',
    acquiredAt: '2026-10-03T00:00:00+00:00',
    expiresAt: '2026-10-03T00:02:00+00:00',
    ...overrides
  }
}

describe('编辑锁的解析与归属', () => {
  it('节点锁与整棵树锁都收下，来源端只影响文案', () => {
    const parsed = parseLeases({ leases: [lease(), lease({ scope: 'tree', targetId: null, client: 'web' })], lifetimeSeconds: 120 })
    expect(parsed.invalidCount).toBe(0)
    expect(parsed.lifetimeSeconds).toBe(120)
    expect(parsed.leases[0].scope).toBe('node')
    expect(parsed.leases[0].targetId).toBe(NODE_A)
    expect(parsed.leases[1].targetId).toBeNull()
    expect(describeLease(parsed.leases[0])).toBe('陈默（桌面端）')
    expect(describeLease(parsed.leases[1])).toBe('陈默（网页端）')
  })

  it('认不出的来源端说「网页端」，与服务端 EditClient.Label 一致', () => {
    expect(clientLabel('desktop')).toBe('桌面端')
    expect(clientLabel('web')).toBe('网页端')
    expect(clientLabel('unknown')).toBe('网页端')
  })

  it('单条坏数据只跳过并计数，不把整份锁列表清空', () => {
    const parsed = parseLeases({ leases: [lease(), { scope: 'node', targetId: NODE_B }, null, { leaseId: 'x' }, lease({ scope: 'node', targetId: null })] })
    expect(parsed.leases).toHaveLength(1)
    expect(parsed.invalidCount).toBe(4)
  })

  it('形状不对就抛，不返回一份「看起来没人编辑」的空列表', () => {
    expect(() => parseLeases(null)).toThrow('响应格式不正确')
    expect(() => parseLeases([])).toThrow('响应格式不正确')
    expect(() => parseLeases({ lifetimeSeconds: 120 })).toThrow('响应格式不正确')
  })

  it('节点锁管一条记录，整棵树锁盖住所有记录，节点锁更具体所以优先', () => {
    const nodeOnly = parseLeases({ leases: [lease()] }).leases
    expect(leaseCovering(nodeOnly, NODE_A)?.targetId).toBe(NODE_A)
    expect(leaseCovering(nodeOnly, NODE_B)).toBeNull()
    expect(nodeLease(nodeOnly, NODE_B)).toBeNull()

    const both = parseLeases({ leases: [lease({ scope: 'tree', targetId: null, leaseId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' }), lease()] }).leases
    expect(treeLease(both)?.scope).toBe('tree')
    // 两条都在时，节点级那条说得更具体，界面该拿它显示「谁在编辑这个节点」
    expect(leaseCovering(both, NODE_A)?.scope).toBe('node')
    expect(leaseCovering(both, NODE_B)?.scope).toBe('tree')
  })

  it('自己的锁要认得出来——「你在编辑」与「别人在编辑」在界面上是两件事', () => {
    const mine = parseLeases({ leases: [lease({ userId: 'me' })] }).leases[0]
    expect(isMine(mine, 'me')).toBe(true)
    expect(isMine(mine, 'someone-else')).toBe(false)
    expect(isMine(null, 'me')).toBe(false)
  })

  it('单把锁与冲突里的持有者用同一套校验', () => {
    expect(parseLease(lease())?.targetId).toBe(NODE_A)
    expect(parseLease({ leaseId: 'x' })).toBeNull()
    expect(parseLease(lease({ scope: 'node', targetId: null }))).toBeNull()
    // 409 的响应体里带着持有者：界面要显示「等谁保存」，不能只有一句「被占用」
    expect(holderOf({ code: 'EDIT_CONFLICT', message: '被占用', holder: lease({ displayName: '陈默' }) })?.displayName).toBe('陈默')
    expect(holderOf({ code: 'EDIT_CONFLICT', message: '被占用' })).toBeNull()
    expect(holderOf(null)).toBeNull()
  })

  it('该不该去占锁：光选中不算，真的打算改才算', () => {
    // 选中一个节点、既没聚焦也没改：不占——否则点着看一圈就撒一地锁
    expect(shouldHoldNodeLease({ editable: true, focused: false, dirty: false })).toBe(false)
    // 焦点进了编辑框：占
    expect(shouldHoldNodeLease({ editable: true, focused: true, dirty: false })).toBe(true)
    // 焦点走了但草稿还在：继续占着，否则一移开焦点别人就能进来改
    expect(shouldHoldNodeLease({ editable: true, focused: false, dirty: true })).toBe(true)
    // 没权限或只读：不占（占了也只是白挨一个 403）
    expect(shouldHoldNodeLease({ editable: false, focused: true, dirty: true })).toBe(false)
  })
})

describe('整理计划的解析', () => {
  const wire = {
    wholeCanvas: true,
    changed: true,
    blocking: false,
    requiresConfirmation: false,
    protectedRecordIds: [],
    lanes: [{ kind: 'Chapter', chapterId: 'ch-1', title: '第一章', order: 10, recordIds: [NODE_A] }],
    changes: [
      { recordId: NODE_A, title: '分镜一', fromX: 1500, fromY: 900, toX: 108, toY: 346 },
      { recordId: NODE_B, title: '分镜二', fromX: 1600, fromY: 1000, toX: 358, toY: 346 },
      { recordId: 'bad', title: '缺坐标' }
    ],
    conflicts: [],
    summary: '整画布泳道布局：2 条泳道，改动 2 项。'
  }

  it('解出逐节点改动与泳道，缺坐标的那条跳过', () => {
    const plan = parseLayoutPlan(wire)
    expect(plan.changed).toBe(true)
    expect(plan.moves).toHaveLength(2)
    expect(plan.moves[0]).toEqual({ recordId: NODE_A, title: '分镜一', from: { x: 1500, y: 900 }, to: { x: 108, y: 346 } })
    expect(plan.lanes[0]).toMatchObject({ kind: 'Chapter', chapterId: 'ch-1', title: '第一章' })
    expect(ghostMoves(plan)).toHaveLength(2)
    expect([...movingIds(plan)].sort()).toEqual([NODE_A, NODE_B])
  })

  it('有阻断冲突就是阻断——即使服务端那个布尔值没跟上', () => {
    const plan = parseLayoutPlan({
      ...wire,
      blocking: false,
      conflicts: [{ recordId: NODE_A, code: 'LAYOUT_MANUAL_OVERLAP', message: '两个手动摆放的节点重叠', blocking: true }]
    })
    expect(plan.blocking).toBe(true)
    // 只要不阻断，冲突也不拦着写入（非阻断冲突是提示）
    expect(parseLayoutPlan({ ...wire, conflicts: [{ recordId: NODE_A, code: 'X', message: '提示', blocking: false }] }).blocking).toBe(false)
  })

  it('要确认覆盖手动摆放时，把受保护的节点一并报出来', () => {
    const plan = parseLayoutPlan({ ...wire, requiresConfirmation: true, protectedRecordIds: [NODE_B] })
    expect(plan.requiresConfirmation).toBe(true)
    expect(plan.protectedRecordIds).toEqual([NODE_B])
  })

  it('形状不对就抛，不返回一份「没有改动」的空计划', () => {
    expect(() => parseLayoutPlan(null)).toThrow('响应格式不正确')
    expect(() => parseLayoutPlan({ wholeCanvas: true })).toThrow('响应格式不正确')
    expect(() => parseLayoutPlan({ wholeCanvas: true, changed: true, changes: {}, lanes: [], conflicts: [] })).toThrow('响应格式不正确')
  })

  it('范围的人话名字', () => {
    expect(scopeLabel('all')).toBe('整画布')
    expect(scopeLabel('ch-1', '第一章')).toBe('第一章')
    expect(scopeLabel('ch-1')).toBe('这一章')
  })
})
