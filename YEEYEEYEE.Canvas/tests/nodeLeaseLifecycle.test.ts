import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useNodeLease } from '../src/shell/useNodeLease'

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

const lease = (leaseId = 'lease-a') => ({ lease: {
  leaseId, scope: 'node', targetId: 'node-a', userId: 'me', displayName: '测试用户',
  client: 'web', acquiredAt: '2026-10-11T00:00:00Z', expiresAt: '2026-10-11T00:02:00Z'
} })
const response = (body: unknown) => new Response(JSON.stringify(body), { status: 200 })
const flush = async () => { for (let i = 0; i < 30; i++) await Promise.resolve() }
const page = (type: string, persisted = false) => {
  const event = new Event(type)
  Object.defineProperty(event, 'persisted', { value: persisted })
  window.dispatchEvent(event)
}
let cleanup: (() => void) | undefined
let fetchMock: ReturnType<typeof vi.fn>
function mount(recordId = 'node-a', wanted = true) {
  hooks.refIndex = 0
  hooks.stateIndex = 0
  useNodeLease({ recordId, wanted })
  cleanup = hooks.effect?.() || undefined
}
function methods() { return fetchMock.mock.calls.map((call) => (call[1] as RequestInit).method) }

beforeEach(() => {
  vi.useFakeTimers()
  hooks.states = []
  hooks.refs = []
  const target = new EventTarget()
  vi.stubGlobal('window', Object.assign(target, {
    location: { search: '?canvas=canvas-test' },
    setInterval: globalThis.setInterval, clearInterval: globalThis.clearInterval
  }))
  fetchMock = vi.fn(async () => response(lease()))
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => {
  cleanup?.()
  cleanup = undefined
  await flush()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('节点租约页面生命周期', () => {
  it('独立编辑周期声明不同 ownerId，同周期重试保持标识', async () => {
    mount()
    await flush()
    const first = JSON.parse(fetchMock.mock.calls[0][1].body).ownerId
    expect(first).toMatch(/^[0-9a-f-]{36}$/)
    fetchMock.mockRejectedValueOnce(new Error('expired'))
    await vi.advanceTimersByTimeAsync(20000)
    expect(JSON.parse(fetchMock.mock.calls[2][1].body).ownerId).toBe(first)
    cleanup?.()
    hooks.refs = []
    mount()
    await flush()
    expect(JSON.parse(fetchMock.mock.calls[4][1].body).ownerId).not.toBe(first)
  })

  it('pagehide 立即按单把租约 keepalive 释放且停止续期，bfcache 恢复重新申请并续期', async () => {
    mount()
    await flush()
    expect(hooks.states[0]).toBe(true)
    page('pagehide', true)
    expect(methods()).toEqual(['POST', 'DELETE'])
    expect(fetchMock.mock.calls[1]).toMatchObject([
      '/api/web/edits/lease-a?canvas=canvas-test', { method: 'DELETE', keepalive: true, credentials: 'same-origin' }
    ])
    expect(hooks.states[0]).toBe(false)
    await vi.advanceTimersByTimeAsync(60000)
    expect(methods()).toEqual(['POST', 'DELETE'])
    page('pageshow', true)
    await flush()
    expect(methods()).toEqual(['POST', 'DELETE', 'POST'])
    expect(JSON.parse(fetchMock.mock.calls[2][1].body).ownerId).toBe(JSON.parse(fetchMock.mock.calls[0][1].body).ownerId)
    await vi.advanceTimersByTimeAsync(20000)
    expect(methods()).toEqual(['POST', 'DELETE', 'POST', 'PUT'])
  })

  it('普通切后台和非 bfcache pageshow 不释放或重复申请', async () => {
    mount()
    await flush()
    page('visibilitychange')
    page('pageshow')
    await vi.advanceTimersByTimeAsync(20000)
    expect(methods()).toEqual(['POST', 'PUT'])
    expect(hooks.states[0]).toBe(true)
  })

  it('pagehide 时申请未返回：晚到租约先释放，再允许恢复周期申请同一幂等租约', async () => {
    let resolve!: (value: Response) => void
    fetchMock.mockImplementationOnce(() => new Promise<Response>((done) => { resolve = done }))
    mount()
    await flush()
    page('pagehide', true)
    page('pageshow', true)
    await flush()
    expect(methods()).toEqual(['POST'])
    resolve(response(lease()))
    await flush()
    expect(methods()).toEqual(['POST', 'DELETE', 'POST'])
    expect(hooks.states[0]).toBe(true)
  })

  it('取消申请后晚返回的租约释放，不能覆盖切换节点后的新周期', async () => {
    let resolve!: (value: Response) => void
    fetchMock.mockImplementationOnce(() => new Promise<Response>((done) => { resolve = done }))
    mount()
    await flush()
    cleanup?.()
    mount('node-b')
    resolve(response(lease('old-lease')))
    await flush()
    expect(methods()).toEqual(['POST', 'DELETE', 'POST'])
    expect(fetchMock.mock.calls[1][0]).toContain('/old-lease?canvas=canvas-test')
    expect(JSON.parse(fetchMock.mock.calls[2][1].body)).toMatchObject({ targetId: 'node-b' })
    expect(hooks.states[0]).toBe(true)
  })

  it('旧续期晚返回不会恢复已隐藏页面的持有状态，恢复等待释放完成', async () => {
    mount()
    await flush()
    let resolveRenew!: (value: Response) => void
    let resolveDelete!: (value: Response) => void
    fetchMock.mockImplementationOnce(() => new Promise<Response>((done) => { resolveRenew = done }))
    await vi.advanceTimersByTimeAsync(20000)
    fetchMock.mockImplementationOnce(() => new Promise<Response>((done) => { resolveDelete = done }))
    page('pagehide', true)
    page('pageshow', true)
    resolveRenew(response(lease('obsolete')))
    await flush()
    expect(hooks.states[0]).toBe(false)
    expect(methods()).toEqual(['POST', 'PUT', 'DELETE'])
    resolveDelete(response({}))
    await flush()
    expect(methods()).toEqual(['POST', 'PUT', 'DELETE', 'POST'])
    expect(hooks.states[0]).toBe(true)
  })

  it('续期失败重新申请；不需要编辑时不申请且 cleanup 移除生命周期监听', async () => {
    mount()
    await flush()
    fetchMock.mockRejectedValueOnce(new Error('expired'))
    await vi.advanceTimersByTimeAsync(20000)
    expect(methods()).toEqual(['POST', 'PUT', 'POST'])
    cleanup?.()
    mount('node-a', false)
    await flush()
    page('pageshow', true)
    page('pagehide')
    await vi.advanceTimersByTimeAsync(40000)
    expect(methods()).toEqual(['POST', 'PUT', 'POST', 'DELETE'])
  })
})
