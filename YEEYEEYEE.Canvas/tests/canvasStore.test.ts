import { describe, expect, it } from 'vitest'
import { CanvasStore } from '../src/CanvasStore'

describe('CanvasStore', () => {
  const op = (id: string) => ({ recordId: id, recordType: 'shape', record: { id } })
  it('按 recordId 幂等去重并维护 revision', () => { const store = new CanvasStore(); store.applyLocal({ batchId: crypto.randomUUID(), baseRevision: 0, source: 'user', ops: [op('a'), op('a')] }); expect(store.getRecords()).toHaveLength(1); expect(store.getRevision()).toBe(1) })
  it('支持单人本地撤销且不丢失无关记录', () => {
    const store = new CanvasStore()
    store.applyLocal({ batchId: crypto.randomUUID(), baseRevision: 0, source: 'ai', ops: [op('a'), op('b')] })
    store.applyLocal({ batchId: crypto.randomUUID(), baseRevision: 1, source: 'user', ops: [{ ...op('a'), record: { id: 'a2' } }] })
    store.undoLocal()
    expect(store.getRecords().map((item) => item.recordId)).toEqual(['a', 'b'])
    expect(store.getRecords().find((item) => item.recordId === 'a')?.record.id).toBe('a')
    store.undoLocal()
    expect(store.getRecords()).toHaveLength(0)
  })
  it('应用远端删除并拒绝修订缺口，等待场景重置', () => {
    const store = new CanvasStore()
    store.reset({ revision: 3, snapshot: { records: [op('a'), op('b')] } })
    expect(() => store.applyRemote({ batchId: crypto.randomUUID(), revision: 5, origin: 'remote', actorSessionId: 'host', ops: [] })).toThrow('SCENE_REVISION_GAP')
    store.applyRemote({ batchId: crypto.randomUUID(), revision: 4, origin: 'remote', actorSessionId: 'host', ops: [{ ...op('a'), deleted: true }] })
    expect(store.getRecords().map((item) => item.recordId)).toEqual(['b'])
  })
  it('拒绝协作撤销', () => { expect(() => new CanvasStore().rejectCollaborativeUndo()).toThrow('PROTOCOL_UNAUTHORIZED') })
})
