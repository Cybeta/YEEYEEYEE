import type { CanvasOpBatch, HostOpBatch, OperationRecord, Scene } from './Protocol/VersionedMessages'

export class CanvasStore {
  private records = new Map<string, OperationRecord>()
  private revision = 0
  private undoStack: Array<Map<string, OperationRecord | null>> = []
  private redoStack: Array<Map<string, OperationRecord | null>> = []

  getRevision(): number { return this.revision }
  getRecords(): OperationRecord[] { return [...this.records.values()] }
  canUndo(): boolean { return this.undoStack.length > 0 }
  canRedo(): boolean { return this.redoStack.length > 0 }

  applyLocal(batch: CanvasOpBatch): void {
    if (batch.baseRevision !== this.revision) throw new Error('SCENE_REVISION_CONFLICT')
    const previous = new Map<string, OperationRecord | null>(batch.ops.map((op) => [op.recordId, this.records.get(op.recordId) ?? null]))
    for (const op of batch.ops) this.applyRecord(op)
    this.undoStack.push(previous)
    this.redoStack = []
    this.revision++
  }

  applyRemote(batch: HostOpBatch): void {
    if (batch.revision !== this.revision + 1) throw new Error('SCENE_REVISION_GAP')
    for (const op of batch.ops) this.applyRecord(op)
    this.revision = batch.revision
  }

  reset(scene: Scene): void {
    this.records = new Map()
    for (const op of scene.snapshot.records) this.applyRecord(op)
    this.revision = scene.revision
    this.undoStack = []
    this.redoStack = []
  }
  undoLocal(): OperationRecord[] | null {
    const previous = this.undoStack.pop()
    if (!previous) return null
    const current = new Map<string, OperationRecord | null>([...previous.keys()].map((id) => [id, this.records.get(id) ?? null]))
    this.redoStack.push(current)
    for (const [id, op] of previous) op ? this.records.set(id, op) : this.records.delete(id)
    return [...previous.values()].filter((op): op is OperationRecord => Boolean(op))
  }
  redoLocal(): OperationRecord[] | null {
    const next = this.redoStack.pop()
    if (!next) return null
    const current = new Map<string, OperationRecord | null>([...next.keys()].map((id) => [id, this.records.get(id) ?? null]))
    this.undoStack.push(current)
    for (const [id, op] of next) op ? this.records.set(id, op) : this.records.delete(id)
    return [...next.values()].filter((op): op is OperationRecord => Boolean(op))
  }
  private applyRecord(op: OperationRecord): void { if (op.deleted) this.records.delete(op.recordId); else this.records.set(op.recordId, op) }
  rejectCollaborativeUndo(): never { throw new Error('PROTOCOL_UNAUTHORIZED') }
}
