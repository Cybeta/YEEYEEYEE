import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useServerEvents } from '../src/shell/useServerEvents'

/**
 * SSE 订阅的**断线恢复**口径。
 *
 * 这条流不是可靠投递：断线期间服务端推过的事件不会重放，所以「自动重连」只保证之后的新推送接得上，
 * 那段空档必须由订阅方自己补取快照。这里钉住的正是那个入口（`onReconnected`）：
 * 首次连上不该补、断过之后每次重连补一次、反复断连仍然只有一条连接、重连后的事件照旧只应用一次。
 */

const hooks = vi.hoisted(() => ({
  effect: undefined as (() => void | (() => void)) | undefined,
  states: [] as unknown[],
  refs: [] as { current: unknown }[],
  stateIndex: 0,
  refIndex: 0
}))
vi.mock('react', () => ({
  useEffect: (effect: () => void | (() => void)) => { hooks.effect = effect },
  useRef: (value: unknown) => {
    const index = hooks.refIndex++
    return hooks.refs[index] ??= { current: value }
  },
  useState: (value: unknown) => {
    const index = hooks.stateIndex++
    if (!(index in hooks.states)) hooks.states[index] = value
    return [hooks.states[index], (next: unknown) => { hooks.states[index] = next }]
  }
}))

/** 只留浏览器 EventSource 里这套钩子用得到的那几样：onopen / onerror / 具名监听 / close。 */
class FakeEventSource {
  static instances: FakeEventSource[] = []
  readonly listeners = new Map<string, ((event: Event) => void)[]>()
  onopen: (() => void) | null = null
  onerror: (() => void) | null = null
  closed = false
  constructor(readonly url: string) { FakeEventSource.instances.push(this) }
  addEventListener(type: string, handler: (event: Event) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), handler])
  }
  removeEventListener(type: string, handler: (event: Event) => void) {
    this.listeners.set(type, (this.listeners.get(type) ?? []).filter((item) => item !== handler))
  }
  close() { this.closed = true }
  /** 服务端把一条具名事件推下来。 */
  dispatch(type: string, data: string) {
    for (const handler of this.listeners.get(type) ?? []) handler({ data } as unknown as Event)
  }
}

let cleanup: (() => void) | undefined

/** 与真实调用方同一种写法：每次渲染把最新的回调交给 hook，hook 自己放 ref。 */
function mount(options: { enabled: boolean; onReconnected?: () => void; onCanvasChanged?: (event: unknown) => void }) {
  hooks.refIndex = 0
  hooks.stateIndex = 0
  useServerEvents(options)
  cleanup = hooks.effect?.() || undefined
}

function source(): FakeEventSource {
  expect(FakeEventSource.instances).toHaveLength(1)
  return FakeEventSource.instances[0]
}

function canvasChanged(revision = 9): string {
  return JSON.stringify({ type: 'canvas.changed', revision, recordId: null, actor: '陈默', scope: 'record' })
}

beforeEach(() => {
  FakeEventSource.instances = []
  hooks.states = []
  hooks.refs = []
  vi.stubGlobal('EventSource', FakeEventSource)
  vi.stubGlobal('window', Object.assign(new EventTarget(), { location: { search: '?canvas=canvas-a' } }))
})
afterEach(() => {
  cleanup?.()
  cleanup = undefined
  vi.unstubAllGlobals()
})

describe('变更推送的断线恢复', () => {
  it('首次连上不补取快照——调用方那时刚取过数据', () => {
    const onReconnected = vi.fn()
    mount({ enabled: true, onReconnected })
    source().onopen?.()
    expect(onReconnected).not.toHaveBeenCalled()
  })

  it('断开之后重连补取一次快照', () => {
    const onReconnected = vi.fn()
    mount({ enabled: true, onReconnected })
    const stream = source()
    stream.onopen?.()
    stream.onerror?.()
    stream.onopen?.()
    expect(onReconnected).toHaveBeenCalledTimes(1)
  })

  it('反复断线重连各补一次，而且始终只有一条连接（不会重复订阅）', () => {
    const onReconnected = vi.fn()
    mount({ enabled: true, onReconnected })
    const stream = source()
    stream.onopen?.()
    for (let round = 0; round < 3; round++) {
      stream.onerror?.()
      stream.onopen?.()
    }
    expect(onReconnected).toHaveBeenCalledTimes(3)
    expect(FakeEventSource.instances).toHaveLength(1)
  })

  it('重连之后到达的事件照旧被应用，且只应用一次', () => {
    const onCanvasChanged = vi.fn()
    mount({ enabled: true, onCanvasChanged })
    const stream = source()
    stream.onopen?.()
    stream.onerror?.()
    stream.onopen?.()
    stream.dispatch('canvas.changed', canvasChanged(21))
    expect(onCanvasChanged).toHaveBeenCalledTimes(1)
    expect(onCanvasChanged.mock.calls[0][0]).toMatchObject({ revision: 21, actor: '陈默' })
  })

  it('断开时如实记下「现在没连上」，关掉订阅之后那条原因也要跟着清掉', () => {
    mount({ enabled: true })
    const stream = source()
    stream.onopen?.()
    stream.onerror?.()
    expect(hooks.states[0]).toBe(false)
    expect(hooks.states[1]).toBe('推送连接断开，正在重连（编辑锁仍按 15 秒轮询兜底）')
    cleanup?.()
    cleanup = undefined
    expect(stream.closed).toBe(true)
    // 退出登录 / 换画布之后再回来，不该先显示一句上一条连接留下的「断开」。
    expect(hooks.states[1]).toBe('')
  })

  it('没登录就不订阅', () => {
    mount({ enabled: false, onReconnected: vi.fn() })
    expect(FakeEventSource.instances).toHaveLength(0)
  })
})
