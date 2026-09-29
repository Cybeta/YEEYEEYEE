import { ERROR_CODES, MESSAGE_DIRECTIONS, PROTOCOL_VERSION, isMessageType, isUuid, type Direction, type Envelope, type ErrorCode, type MessageType } from './VersionedMessages'

export class ProtocolViolationError extends Error {
  constructor(public readonly code: ErrorCode, message: string, public readonly severity: 'warning' | 'fatal' = 'fatal') { super(message); this.name = 'ProtocolViolationError' }
}

const requiredPayload: Partial<Record<MessageType, string[]>> = {
  'canvas/hello': ['canvasVersion', 'protocolVersion', 'minHostProtocol', 'features'],
  'canvas/op.batch': ['batchId', 'baseRevision', 'source', 'ops'], 'canvas/undo.request': ['localOnly'], 'canvas/redo.request': ['localOnly'], 'canvas/invoke.request': ['invocation', 'idempotencyKey'], 'canvas/resource.replace.request': ['recordId', 'entityId', 'variantId', 'variantVersionId'],
  'canvas/job.cancel.request': ['jobId'], 'host/init': ['protocolVersion', 'hostVersion', 'session', 'capabilities', 'scene', 'locale'],
  'host/op.batch': ['batchId', 'revision', 'origin', 'actorSessionId', 'ops'], 'host/scene.reset': ['revision', 'reason', 'scene'],
  'host/undo.result': ['ok', 'localOnly', 'revision', 'reason'], 'host/job.update': ['jobId', 'invocationId', 'state', 'progressPercent', 'outputs'], 'host/resource.replace.result': ['requestId', 'ok', 'message'],
  'host/capabilities': ['serverClaims', 'canEditCanvas', 'canInvokeSkill', 'canCancelJob', 'canUndo'], 'host/error': ['code', 'message', 'severity']
}

function object(value: unknown): Record<string, unknown> { if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_MALFORMED, '载荷必须为 JSON 对象'); return value as Record<string, unknown> }
function validatePayload(type: MessageType, payload: unknown): void {
  const data = object(payload)
  for (const key of requiredPayload[type] ?? []) if (!(key in data) || data[key] === undefined) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_MALFORMED, `${type} 缺少字段 ${key}`)
  if ((type === 'canvas/undo.request' || type === 'canvas/redo.request') && data.localOnly !== true) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_UNAUTHORIZED, `${type} 只能请求本地撤销`,'warning')
  if (type === 'host/undo.result' && data.localOnly !== true) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_FAIL_CLOSED, '宿主不得返回协作撤销结果')
  if (type === 'canvas/invoke.request' && typeof data.idempotencyKey !== 'string' || type === 'canvas/invoke.request' && (data.idempotencyKey as string).length === 0) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_MALFORMED, '执行请求缺少幂等键')
  if (type === 'host/job.update' && (typeof data.progressPercent !== 'number' || data.progressPercent < 0 || data.progressPercent > 100 || (data.externalTaskId !== undefined && data.externalTaskId !== null && typeof data.externalTaskId !== 'string'))) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_MALFORMED, 'Job 进度或外部任务 ID 无效')
  // 尝试次数与重试血缘（目标 5）：要么不出现，要么必须是 >= 1 的整数 ID/数值，避免两端看到不同的尝试语义。
  if (type === 'host/job.update') {
    if (data.attempt !== undefined && data.attempt !== null && (typeof data.attempt !== 'number' || !Number.isInteger(data.attempt) || data.attempt < 1)) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_MALFORMED, 'Job 尝试次数无效')
    for (const field of ['retryOfJobId', 'rootJobId']) {
      const value = data[field]
      if (value !== undefined && value !== null && typeof value !== 'string') throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_MALFORMED, `Job ${field} 无效`)
    }
  }
  if (type === 'host/init') {
    if (data.protocolVersion !== PROTOCOL_VERSION) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_VERSION_MISMATCH, '宿主协议版本不匹配')
    const session = object(data.session)
    const capabilities = object(data.capabilities)
    const claims = session.serverClaims
    const declared = capabilities.serverClaims
    if (!Array.isArray(claims) || !claims.every((claim) => typeof claim === 'string') || !Array.isArray(declared) || !declared.every((claim) => typeof claim === 'string')) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_UNAUTHORIZED, '会话声明集不是服务端签发的可信声明')
    if (claims.some((claim) => !declared.includes(claim))) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_UNAUTHORIZED, '会话声明超出能力声明集')
    if (typeof session.role === 'string' && session.role === 'Owner' && declared.length === 0) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_UNAUTHORIZED, '客户端角色不能在无服务端声明时提升权限')
    if (typeof session.role !== 'string' || typeof session.clientType !== 'string') throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_MALFORMED, '会话上下文字段无效')
    const claimMap: Record<string, string> = { canEditCanvas: 'canvas.edit', canInvokeSkill: 'skill.invoke', canCancelJob: 'job.cancel', canUndo: 'canvas.undo' }
    for (const key of Object.keys(claimMap)) if (capabilities[key] === true && !declared.includes(claimMap[key])) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_UNAUTHORIZED, `能力位 ${key} 超出服务端声明集`)
  }
}

export function decode(input: string | unknown, direction: Direction): Envelope {
  let value: unknown
  try { value = typeof input === 'string' ? JSON.parse(input) : input } catch { throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_MALFORMED, '消息不是有效 JSON') }
  let data: Record<string, unknown>
  try { data = object(value) } catch { throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_MALFORMED, '消息信封必须为 JSON 对象') }
  if (!('v' in data) || !('id' in data) || !('type' in data) || !('ts' in data) || !('payload' in data) || data.payload === null || !isUuid(data.id) || typeof data.ts !== 'number' || typeof data.v !== 'number' || typeof data.type !== 'string') throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_MALFORMED, '消息信封字段无效')
  if (data.v !== PROTOCOL_VERSION) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_VERSION_MISMATCH, `协议版本 ${data.v} 不受支持`)
  if (!isMessageType(data.type)) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_UNKNOWN_TYPE, `未知消息类型 ${data.type}`)
  if (MESSAGE_DIRECTIONS[data.type] !== direction) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_DIRECTION_MISMATCH, `消息 ${data.type} 方向错误`)
  validatePayload(data.type, data.payload)
  return data as unknown as Envelope
}

export function encode<T>(message: Envelope<T>, direction: Direction): string { decode(message, direction); return JSON.stringify(message) }
