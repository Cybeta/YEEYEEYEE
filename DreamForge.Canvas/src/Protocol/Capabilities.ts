import { ERROR_CODES, type CapabilitySet, type HostInit } from './VersionedMessages'
import { ProtocolViolationError } from './CanvasMessageCodec'

const CLAIMS: Record<keyof Omit<CapabilitySet, 'serverClaims' | 'reason'>, string> = {
  canEditCanvas: 'canvas.edit', canInvokeSkill: 'skill.invoke', canCancelJob: 'job.cancel', canUndo: 'canvas.undo'
}

export function validateCapabilities(capabilities: CapabilitySet): CapabilitySet {
  for (const key of Object.keys(CLAIMS) as Array<keyof typeof CLAIMS>) {
    if (capabilities[key] && !capabilities.serverClaims.includes(CLAIMS[key])) {
      throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_UNAUTHORIZED, `能力位 ${key} 超出服务端声明集`, 'fatal')
    }
  }
  return capabilities
}

export function validateHostInit(init: HostInit): HostInit {
  if (init.protocolVersion !== 1) throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_VERSION_MISMATCH, '宿主协议版本不匹配', 'fatal')
  validateCapabilities(init.capabilities)
  if (init.session.serverClaims.some((claim) => !init.capabilities.serverClaims.includes(claim))) {
    throw new ProtocolViolationError(ERROR_CODES.PROTOCOL_UNAUTHORIZED, '会话声明与能力声明不一致', 'fatal')
  }
  return init
}

export function canEdit(capabilities: CapabilitySet): boolean { return capabilities.canEditCanvas }
export function canInvoke(capabilities: CapabilitySet): boolean { return capabilities.canInvokeSkill }
