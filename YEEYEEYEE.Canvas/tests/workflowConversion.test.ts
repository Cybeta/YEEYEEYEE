import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { startWorkflowConversion } from '../src/shell/workflowConversion'

const session = { id: 'session/a', requestId: 'request-a', url: 'https://convert.example/convert', origin: 'https://convert.example' }
const imported = { converted: 1, failed: 0, workflows: [{ key: 'a', title: '工作流', converted: true, note: '', error: '' }] }
const response = (body: unknown) => new Response(JSON.stringify(body))
const flush = () => vi.advanceTimersByTimeAsync(0)
let target: EventTarget
let popup: { closed: boolean; close: ReturnType<typeof vi.fn>; location: { href: string } }
let fetchMock: ReturnType<typeof vi.fn>
let operation: ReturnType<typeof startWorkflowConversion> | undefined
function message(overrides: Record<string, unknown> = {}, origin = session.origin, source: unknown = popup) {
  const event = new Event('message')
  Object.assign(event, { origin, source, data: { kind: 'comfyui-conversion', session: session.id, requestId: session.requestId, success: true, ...overrides } })
  target.dispatchEvent(event)
}
function start(timeout?: number) {
  operation = startWorkflowConversion(['one.json'], 'https://comfy.example', timeout)
  void operation.result.catch(() => {})
  return operation
}
beforeEach(() => {
  vi.useFakeTimers()
  target = new EventTarget()
  popup = { closed: false, close: vi.fn(), location: { href: 'about:blank' } }
  vi.stubGlobal('window', Object.assign(target, {
    location: { search: '?canvas=test' }, open: vi.fn(() => popup),
    setInterval: globalThis.setInterval, clearInterval: globalThis.clearInterval,
    setTimeout: globalThis.setTimeout, clearTimeout: globalThis.clearTimeout
  }))
  fetchMock = vi.fn(async () => response(session))
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => {
  operation?.cancel()
  await flush()
  operation = undefined
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('官方工作流转换会话', () => {
  it('同步打开随机命名窗口，注册监听后才导航，固定地址和路径快照', async () => {
    const order: string[] = []
    vi.spyOn(window, 'addEventListener').mockImplementation((...args: Parameters<typeof window.addEventListener>) => {
      order.push('listen'); EventTarget.prototype.addEventListener.apply(target, args)
    })
    Object.defineProperty(popup.location, 'href', { set: () => { order.push('navigate'); message() } })
    fetchMock.mockResolvedValueOnce(response(session)).mockResolvedValueOnce(response(imported))
    const current = start()
    expect(window.open).toHaveBeenCalledWith('about:blank', expect.stringMatching(/^comfyui-/))
    expect(fetchMock.mock.calls[0][1].body).toBe(JSON.stringify({ paths: ['one.json'], baseUrl: 'https://comfy.example' }))
    await expect(current.result).resolves.toEqual(imported)
    expect(order).toEqual(['listen', 'navigate'])
    expect(popup.close).toHaveBeenCalledOnce()
  })

  it('忽略来源、窗口、会话、请求标识和消息类型不匹配，只完成一次', async () => {
    const current = start()
    await flush()
    message({}, 'https://evil.example')
    message({}, session.origin, {})
    message({ session: 'other' })
    message({ requestId: 'other' })
    message({ kind: 'other' })
    message({ success: 'true' })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    fetchMock.mockResolvedValueOnce(response(imported))
    message(); message()
    await expect(current.result).resolves.toEqual(imported)
    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(fetchMock.mock.calls[1]).toMatchObject(['/api/web/settings/workflow-conversions/session%2Fa/complete?canvas=test', { method: 'POST' }])
    expect(vi.getTimerCount()).toBe(0)
  })

  it.each(['取消/卸载', '窗口关闭', '超时', '转换失败'])('%s 清理监听、定时器并删除会话', async reason => {
    const current = start(1000)
    await flush()
    if (reason === '取消/卸载') current.cancel()
    if (reason === '窗口关闭') { popup.closed = true; await vi.advanceTimersByTimeAsync(500) }
    if (reason === '超时') await vi.advanceTimersByTimeAsync(1000)
    if (reason === '转换失败') message({ success: false, error: '转换错误' })
    await expect(current.result).rejects.toThrow()
    message()
    current.cancel()
    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(fetchMock.mock.calls[1]).toMatchObject(['/api/web/settings/workflow-conversions/session%2Fa?canvas=test', { method: 'DELETE', keepalive: true }])
    expect(popup.close).toHaveBeenCalledOnce()
    expect(vi.getTimerCount()).toBe(0)
  })

  it('创建未返回时取消，晚到会话立即删除且不导航', async () => {
    let resolve!: (value: Response) => void
    fetchMock.mockImplementationOnce(() => new Promise<Response>(done => { resolve = done }))
    const current = start()
    current.cancel()
    await expect(current.result).rejects.toThrow('取消')
    resolve(response(session))
    await flush()
    expect(popup.location.href).toBe('about:blank')
    expect(fetchMock.mock.calls[1][1].method).toBe('DELETE')
  })

  it('完成请求期间取消中止请求，不接收晚到完成结果', async () => {
    const current = start()
    await flush()
    let resolve!: (value: Response) => void
    fetchMock.mockImplementationOnce(() => new Promise<Response>(done => { resolve = done }))
    message()
    const signal = fetchMock.mock.calls[1][1].signal as AbortSignal
    current.cancel()
    expect(signal.aborted).toBe(true)
    resolve(response(imported))
    await expect(current.result).rejects.toThrow('取消')
    await flush()
    expect(fetchMock.mock.calls.map(call => call[1].method)).toEqual(['POST', 'POST', 'DELETE'])
  })

  it('拦截弹窗时不创建会话', () => {
    vi.mocked(window.open).mockReturnValueOnce(null)
    expect(() => start()).toThrow('拦截')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('拒绝会话导航地址与声明来源不一致', async () => {
    fetchMock.mockResolvedValueOnce(response({ ...session, url: 'https://other.example/convert' }))
    const current = start()
    await expect(current.result).rejects.toThrow('来源无效')
    expect(popup.location.href).toBe('about:blank')
    expect(fetchMock.mock.calls[1][1].method).toBe('DELETE')
  })
})
