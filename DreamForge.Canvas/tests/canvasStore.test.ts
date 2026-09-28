import { describe, expect, it } from 'vitest'
import { CanvasStore } from '../src/CanvasStore'

describe('CanvasStore', () => {
  const op = (id: string) => ({ recordId: id, recordType: 'shape', record: { id } })
  it('按 recordId 幂等去重并维护 revision', () => { const store = new CanvasStore(); store.applyLocal({ batchId: crypto.randomUUID(), baseRevision: 0, source: 'user', ops: [op('a'), op('a')] }); expect(store.getRecords()).toHaveLength(1); expect(store.getRevision()).toBe(1) })
  it('支持单人本地撤销', () => { const store = new CanvasStore(); store.applyLocal({ batchId: crypto.randomUUID(), baseRevision: 0, source: 'ai', ops: [op('a')] }); expect(store.canUndo()).toBe(true); store.undoLocal(); expect(store.getRecords()).toHaveLength(0) })
  it('拒绝协作撤销', () => { expect(() => new CanvasStore().rejectCollaborativeUndo()).toThrow('PROTOCOL_UNAUTHORIZED') })
})
