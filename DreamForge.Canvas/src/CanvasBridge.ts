import { validateCapabilities, validateHostInit } from './Protocol/Capabilities'
import { decode, encode, ProtocolViolationError } from './Protocol/CanvasMessageCodec'
import { CANVAS_VERSION, PROTOCOL_VERSION, type CanvasBridgeTransport, type Envelope, type HostInit, type HostError, type HostOpBatch, type CapabilitySet, type MessageType } from './Protocol/VersionedMessages'

type BridgeState = 'created' | 'handshaking' | 'ready' | 'fatal'
export type CanvasBridgeEvents = {
  onInit?: (init: HostInit) => void
  onSceneReset?: (scene: HostInit['scene']) => void
  onHostOps?: (batch: HostOpBatch) => void
}

export class CanvasBridge {
  private state: BridgeState = 'created'
  private capabilities: CapabilitySet = { serverClaims: [], canEditCanvas: false, canInvokeSkill: false, canCancelJob: false, canUndo: false, reason: '等待宿主初始化' }
  private readonly unsubscribe: () => void
  constructor(private readonly transport: CanvasBridgeTransport, private readonly events: CanvasBridgeEvents = {}) {
    this.unsubscribe = transport.subscribe((message) => this.receive(message))
  }
  getState(): BridgeState { return this.state }
  getCapabilities(): CapabilitySet { return this.capabilities }
  start(): void { if (this.state !== 'created') return; this.state = 'handshaking'; this.send('canvas/hello', { canvasVersion: CANVAS_VERSION, protocolVersion: PROTOCOL_VERSION, minHostProtocol: PROTOCOL_VERSION, features: ['op.batch', 'local.undo', 'job.cancel'] }) }
  dispose(): void { this.unsubscribe() }
  send<T>(type: MessageType & `canvas/${string}`, payload: T): void { if (this.state === 'fatal') return; const message = { v: PROTOCOL_VERSION, id: crypto.randomUUID(), type, ts: Date.now(), payload } as Envelope<T>; this.transport.send(JSON.parse(encode(message, 'canvasToHost'))) }
  receive(raw: unknown): void {
    if (this.state === 'fatal') return
    try {
      const message = decode(raw, 'hostToCanvas')
      if (message.type === 'host/init') { const init = validateHostInit(message.payload as HostInit); this.capabilities = init.capabilities; this.state = 'ready'; this.events.onInit?.(init) }
      else if (message.type === 'host/op.batch') this.events.onHostOps?.(message.payload as HostOpBatch)
      else if (message.type === 'host/scene.reset') this.events.onSceneReset?.((message.payload as { scene: HostInit['scene'] }).scene)
      else if (message.type === 'host/capabilities') this.capabilities = validateCapabilities(message.payload as CapabilitySet)
      else if (message.type === 'host/error' && (message.payload as HostError).severity === 'fatal') this.state = 'fatal'
    } catch (error) {
      if (error instanceof ProtocolViolationError && error.severity === 'fatal') this.state = 'fatal'
    }
  }
}
