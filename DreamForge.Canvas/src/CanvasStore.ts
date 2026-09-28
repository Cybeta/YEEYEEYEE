import type { CanvasOpBatch, HostOpBatch, OperationRecord, Scene } from './Protocol/VersionedMessages'

export class CanvasStore {
  private records = new Map<string, OperationRecord>()
  private revision = 0
  private undoStack: OperationRecord[][] = []
  private redoStack: OperationRecord[][] = []

  getRevision(): number { return this.revision }
  getRecords(): OperationRecord[] { return [...this.records.values()] }
  canUndo(): boolean { return this.undoStack.length > 0 }
  canRedo(): boolean { return this.redoStack.length > 0 }

  applyLocal(batch: CanvasOpBatch): void {
    if (batch.baseRevision !== this.revision) throw new Error('SCENE_REVISION_CONFLICT')
    const previous = batch.ops.map((op) => this.records.get(op.recordId)).filter((op): op is OperationRecord => Boolean(op))
    for (const op of batch.ops) this.records.set(op.recordId, op)
    this.undoStack.push(previous)
    this.redoStack = []
    this.revision++
  }

  applyRemote(batch: HostOpBatch): void {
    for (const op of batch.ops) this.records.set(op.recordId, op)
    this.revision = Math.max(this.revision, batch.revision)
  }

  reset(scene: Scene): void { this.records = new Map(scene.snapshot.records.map((op) => [op.recordId, op])); this.revision = scene.revision; this.undoStack = []; this.redoStack = [] }
  undoLocal(): OperationRecord[] | null { const previous = this.undoStack.pop(); if (!previous) return null; const current = this.getRecords(); this.redoStack.push(current); this.records = new Map(previous.map((op) => [op.recordId, op])); return previous }
  redoLocal(): OperationRecord[] | null { const next = this.redoStack.pop(); if (!next) return null; this.undoStack.push(this.getRecords()); this.records = new Map(next.map((op) => [op.recordId, op])); return next }
  rejectCollaborativeUndo(): never { throw new Error('PROTOCOL_UNAUTHORIZED') }
}
