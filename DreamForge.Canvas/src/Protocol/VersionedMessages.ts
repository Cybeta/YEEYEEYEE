export const PROTOCOL_VERSION = 1 as const
export const CANVAS_VERSION = '0.1.0'

export type Direction = 'canvasToHost' | 'hostToCanvas'
export type MessageType =
  | 'canvas/hello' | 'canvas/op.batch' | 'canvas/undo.request' | 'canvas/redo.request'
  | 'canvas/invoke.request' | 'canvas/job.cancel.request' | 'canvas/selection.changed' | 'canvas/diagnostic'
  | 'host/init' | 'host/op.batch' | 'host/scene.reset' | 'host/undo.result' | 'host/job.update'
  | 'host/capabilities' | 'host/error'

export const MESSAGE_DIRECTIONS: Record<MessageType, Direction> = {
  'canvas/hello': 'canvasToHost', 'canvas/op.batch': 'canvasToHost', 'canvas/undo.request': 'canvasToHost',
  'canvas/redo.request': 'canvasToHost', 'canvas/invoke.request': 'canvasToHost',
  'canvas/job.cancel.request': 'canvasToHost', 'canvas/selection.changed': 'canvasToHost', 'canvas/diagnostic': 'canvasToHost',
  'host/init': 'hostToCanvas', 'host/op.batch': 'hostToCanvas', 'host/scene.reset': 'hostToCanvas',
  'host/undo.result': 'hostToCanvas', 'host/job.update': 'hostToCanvas', 'host/capabilities': 'hostToCanvas', 'host/error': 'hostToCanvas'
}

export const ERROR_CODES = {
  PROTOCOL_VERSION_MISMATCH: 'PROTOCOL_VERSION_MISMATCH', PROTOCOL_UNKNOWN_TYPE: 'PROTOCOL_UNKNOWN_TYPE',
  PROTOCOL_DIRECTION_MISMATCH: 'PROTOCOL_DIRECTION_MISMATCH', PROTOCOL_MALFORMED: 'PROTOCOL_MALFORMED',
  PROTOCOL_UNAUTHORIZED: 'PROTOCOL_UNAUTHORIZED', PROTOCOL_FAIL_CLOSED: 'PROTOCOL_FAIL_CLOSED',
  SCENE_REVISION_CONFLICT: 'SCENE_REVISION_CONFLICT', JOB_NOT_FOUND: 'JOB_NOT_FOUND',
  JOB_NOT_CANCELLABLE: 'JOB_NOT_CANCELLABLE', REFERENCE_UNRESOLVED: 'REFERENCE_UNRESOLVED',
  REFERENCE_UNAUTHORIZED: 'REFERENCE_UNAUTHORIZED', REFERENCE_VERSION_UNSATISFIED: 'REFERENCE_VERSION_UNSATISFIED',
  REFERENCE_RECURSION_LIMIT: 'REFERENCE_RECURSION_LIMIT', REFERENCE_CYCLE: 'REFERENCE_CYCLE'
} as const
export type ErrorCode = typeof ERROR_CODES[keyof typeof ERROR_CODES]

export interface Envelope<T = unknown> { v: number; id: string; replyTo?: string | null; type: MessageType | string; ts: number; payload: T }
export interface CapabilitySet { serverClaims: string[]; canEditCanvas: boolean; canInvokeSkill: boolean; canCancelJob: boolean; canUndo: boolean; reason?: string | null }
export interface TypedReference { kind: 'Channel' | 'Tool' | 'Skill' | 'Asset'; targetId: string; versionConstraint: string; dependencies: TypedReference[] }
export interface OperationRecord { recordId: string; recordType: string; record: Record<string, unknown> }
export interface CanvasOpBatch { batchId: string; baseRevision: number; source: 'user' | 'ai'; ops: OperationRecord[] }
export interface Scene { revision: number; snapshot: { records: OperationRecord[] } }
export interface HostSession { sessionId: string; userId: string; clientType: string; role: string; serverClaims: string[] }
export interface HostInit { protocolVersion: number; hostVersion: string; session: HostSession; capabilities: CapabilitySet; scene: Scene; locale: string }
export interface HostOpBatch { batchId: string; revision: number; origin: 'remote' | 'host'; actorSessionId: string; ops: OperationRecord[] }
export type JobState = 'Queued' | 'Running' | 'Cancelling' | 'Succeeded' | 'Failed' | 'Cancelled'
export interface JobUpdate { jobId: string; invocationId: string; state: JobState; progressPercent: number; errorCode?: string | null; errorMessage?: string | null; externalTaskId?: string | null; outputs: Array<Record<string, unknown>> }
export interface HostError { code: ErrorCode; message: string; severity: 'warning' | 'fatal'; relatedType?: string | null }
export interface CanvasBridgeTransport { send(message: Envelope): void; subscribe(handler: (message: unknown) => void): () => void }

export function isMessageType(value: unknown): value is MessageType { return typeof value === 'string' && value in MESSAGE_DIRECTIONS }
export function isUuid(value: unknown): value is string { return typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value) }
