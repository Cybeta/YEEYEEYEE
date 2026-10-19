import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useCanvasJobs } from '../src/shell/useCanvasJobs'

/**
 * 生成任务的轮询与它交给调用方的两个时机。
 *
 * 这一份测试盯的是**时间的可靠性**，不是渲染：
 * · 推送（SSE）断了以后，兜底轮询还得照常刷新——上线时它才是唯一的发现路径；
 * · 卸载 / 换画布之后迟到的响应不能写进新一份状态（否则旧画布的任务会跑到新画布上）；
 * · 只有「任务刚落终态」与「产物刚从回挂中结束」才让调用方去重取画布，
 *   只是进度在动就重取整张画布，等于拿轮询刷屏。
 *
 * 做法与 nodeLeaseLifecycle 一致：把 React 的三个钩子换成可手驱的版本，
 * 自己按 React 的次序跑 effect / 清理，这样时间推进完全可控。
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
  },
  useCallback: (callback: unknown) => callback
}))

const response = (body: unknown) => new Response(JSON.stringify(body), { status: 200 })
const flush = async () => { for (let i = 0; i < 30; i++) await Promise.resolve() }

let cleanup: (() => void) | undefined
let fetchMock: ReturnType<typeof vi.fn>

/** 一次渲染：跑一遍钩子，然后按 React 的次序换掉 effect（先清理旧的、再挂新的）。 */
function render(enabled = true, onSettled?: () => void) {
  hooks.stateIndex = 0
  hooks.refIndex = 0
  const result = useCanvasJobs(enabled, onSettled)
  cleanup?.()
  cleanup = hooks.effect?.() || undefined
  return result
}

beforeEach(() => {
  vi.useFakeTimers()
  hooks.states = []
  hooks.refs = []
  const target = new EventTarget()
  vi.stubGlobal('window', Object.assign(target, {
    location: { search: '?canvas=canvas-test' },
    setInterval: globalThis.setInterval,
    clearInterval: globalThis.clearInterval
  }))
  fetchMock = vi.fn(async () => response({ jobs: [] }))
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => {
  cleanup?.()
  cleanup = undefined
  await flush()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('生成任务的轮询兜底', () => {
  it('没有推送时也照常刷新：空闲 10 秒一次', async () => {
    render(true)
    await flush()
    expect(fetchMock).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(10000)
    expect(fetchMock).toHaveBeenCalledTimes(2)
    await vi.advanceTimersByTimeAsync(10000)
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('有任务在跑时切到 1.5 秒一次：产物回挂那几秒不该让人等 10 秒', async () => {
    fetchMock.mockImplementation(async () => response({
      jobs: [{ jobId: 'j', state: 'Running', targetNodeId: 'A', progressPercent: 5 }]
    }))
    render(true)
    await flush()
    expect(hooks.states[2]).toBe(true)
    render(true)
    await flush()
    const afterMount = fetchMock.mock.calls.length
    await vi.advanceTimersByTimeAsync(1500)
    expect(fetchMock.mock.calls.length).toBe(afterMount + 1)
  })

  it('退出登录 / 关掉这一份订阅：清空任务，也不再发请求', async () => {
    fetchMock.mockImplementation(async () => response({ jobs: [{ jobId: 'j', state: 'Running', targetNodeId: 'A' }] }))
    render(true)
    await flush()
    expect(hooks.states[0]).toHaveLength(1)
    const calls = fetchMock.mock.calls.length
    render(false)
    expect(hooks.states[0]).toEqual([])
    expect(hooks.states[2]).toBe(false)
    await vi.advanceTimersByTimeAsync(30000)
    expect(fetchMock.mock.calls.length).toBe(calls)
  })
})

describe('卸载 / 换画布之后迟到的响应', () => {
  it('迟到的任务列表不写进新一份状态；重新挂载后只有新一次的响应说了算', async () => {
    let resolveLate!: (value: Response) => void
    fetchMock.mockImplementationOnce(() => new Promise<Response>((done) => { resolveLate = done }))
    render(true)
    await flush()
    expect(hooks.states[0]).toEqual([])

    // 卸载（换画布时 ProjectWorkbench 会先卸掉旧的这一份）。
    cleanup?.()
    cleanup = undefined
    resolveLate(response({ jobs: [{ jobId: 'old', state: 'Running', targetNodeId: 'A' }] }))
    await flush()
    expect(hooks.states[0]).toEqual([])

    fetchMock.mockImplementationOnce(async () => response({ jobs: [{ jobId: 'new', state: 'Running', targetNodeId: 'B' }] }))
    render(true)
    await flush()
    expect(hooks.states[0]).toEqual([{ jobId: 'new', state: 'Running', targetNodeId: 'B', outputs: [], quality: [] }])
  })

  it('只剩最后发出的那一次算数：先发的晚回来也不许覆盖后发的', async () => {
    let resolveFirst!: (value: Response) => void
    fetchMock.mockImplementationOnce(() => new Promise<Response>((done) => { resolveFirst = done }))
    render(true)
    await flush()
    fetchMock.mockImplementationOnce(async () => response({ jobs: [{ jobId: 'second', state: 'Queued', targetNodeId: 'B' }] }))
    render(true)
    await flush()
    expect(hooks.states[0]).toEqual([{ jobId: 'second', state: 'Queued', targetNodeId: 'B', outputs: [], quality: [] }])

    resolveFirst(response({ jobs: [{ jobId: 'first', state: 'Running', targetNodeId: 'A' }] }))
    await flush()
    expect(hooks.states[0]).toEqual([{ jobId: 'second', state: 'Queued', targetNodeId: 'B', outputs: [], quality: [] }])
  })
})

describe('什么时候让调用方去重取画布', () => {
  it('任务刚落终态、以及产物刚从回挂中结束各回调一次；只是进度在动不回调', async () => {
    const settled = vi.fn()
    let jobs: unknown[] = [{ jobId: 'j', state: 'Running', targetNodeId: 'A', progressPercent: 10 }]
    fetchMock.mockImplementation(async () => response({ jobs }))

    render(true, settled)
    await flush()
    expect(settled).not.toHaveBeenCalled()

    jobs = [{ jobId: 'j', state: 'Running', targetNodeId: 'A', progressPercent: 80 }]
    render(true, settled)
    await flush()
    expect(settled).not.toHaveBeenCalled()

    jobs = [{ jobId: 'j', state: 'Succeeded', targetNodeId: 'A', attachmentState: 'pending', outputs: [{ role: 'image', ref: 'a' }] }]
    render(true, settled)
    await flush()
    expect(settled).toHaveBeenCalledTimes(1)

    jobs = [{ jobId: 'j', state: 'Succeeded', targetNodeId: 'A', attachmentState: 'attached', outputs: [{ role: 'image', ref: 'a' }] }]
    render(true, settled)
    await flush()
    expect(settled).toHaveBeenCalledTimes(2)
  })

  it('取任务失败时把原因留下来，下一轮成功就清掉', async () => {
    fetchMock.mockImplementationOnce(async () => new Response(JSON.stringify({ code: 'X', message: '读不到任务' }), { status: 503 }))
    render(true)
    await flush()
    expect(String(hooks.states[1])).toContain('读不到任务')
    await vi.advanceTimersByTimeAsync(10000)
    await flush()
    expect(hooks.states[1]).toBe('')
  })

  it('第一轮列表里就已经回挂完成的任务不重复刷新：那一轮手上这份场景就是刚载的', async () => {
    const settled = vi.fn()
    fetchMock.mockImplementation(async () => response({
      jobs: [{ jobId: 'j', state: 'Succeeded', targetNodeId: 'A', attachmentState: 'attached', outputs: [{ role: 'image', ref: 'a' }] }]
    }))
    render(true, settled)
    await flush()
    expect(settled).not.toHaveBeenCalled()
  })

  it('之后才第一次看到的、已经回挂完成的任务要触发刷新（推送断了时这是唯一的发现路径）', async () => {
    const settled = vi.fn()
    let jobs: unknown[] = []
    fetchMock.mockImplementation(async () => response({ jobs }))

    render(true, settled)
    await flush()
    expect(settled).not.toHaveBeenCalled()

    // 第二轮才出现、而且一出现就已经挂好了：这正是「任务在别处发起 + 推送断开」的样子。
    jobs = [{ jobId: 'j', state: 'Succeeded', targetNodeId: 'A', attachmentState: 'attached', outputs: [{ role: 'image', ref: 'a' }] }]
    render(true, settled)
    await flush()
    expect(settled).toHaveBeenCalledTimes(1)

    // 记下来之后不再重复回调。
    render(true, settled)
    await flush()
    expect(settled).toHaveBeenCalledTimes(1)

    // 只是「第一次看到、还没结束」的不算已经产生产物，不刷。
    jobs = [...jobs, { jobId: 'k', state: 'Running', targetNodeId: 'A', progressPercent: 10 }]
    render(true, settled)
    await flush()
    expect(settled).toHaveBeenCalledTimes(1)
  })
})
