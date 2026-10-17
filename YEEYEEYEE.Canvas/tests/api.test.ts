import { describe, expect, it, vi } from 'vitest'
import { canvasUrl, request, SESSION_LOST_EVENT } from '../src/api'

function windowRecording(): { target: EventTarget; events: string[] } {
  const target = new EventTarget()
  const events: string[] = []
  target.addEventListener(SESSION_LOST_EVENT, () => events.push(SESSION_LOST_EVENT))
  vi.stubGlobal('window', Object.assign(target, { location: { search: '?canvas=canvas-A' } }))
  return { target, events }
}

describe('HTTP request headers', () => {
  it('binds scene, lease, history, chat, SSE and media URLs to the selected canvas', () => {
    vi.stubGlobal('window', { location: { search: '?canvas=canvas-A' } })
    try {
      for (const path of ['/api/web/scene', '/api/web/edits', '/api/web/canvas/history', '/api/web/chat/sessions', '/api/web/events', '/api/web/records/node/attachments/image/file']) {
        expect(canvasUrl(path)).toBe(`${path}?canvas=canvas-A`)
      }
      expect(canvasUrl('/api/web/entities/entity/thumb?variantId=v')).toBe('/api/web/entities/entity/thumb?variantId=v&canvas=canvas-A')
      expect(canvasUrl('/api/web/projects')).toBe('/api/web/projects')
      expect(canvasUrl('/api/auth/me')).toBe('/api/auth/me')
    } finally { vi.unstubAllGlobals() }
  })

  it('merges custom setup headers without putting the setup token in the JSON body', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      expect(init?.headers).toBeInstanceOf(Headers)
      const headers = new Headers(init?.headers)
      expect(headers.get('Content-Type')).toBe('application/json')
      expect(headers.get('X-Setup-Token')).toBe('setup-secret')
      expect(init?.body).toBe(JSON.stringify({ username: 'lin', password: 'longenough', displayName: '' }))
      expect(init?.body).not.toContain('setup-secret')
      return new Response(JSON.stringify({ ok: true }), { status: 200 })
    })
    vi.stubGlobal('fetch', fetchMock)

    await expect(request('/api/auth/setup', {
      method: 'POST',
      headers: { 'X-Setup-Token': 'setup-secret' },
      body: JSON.stringify({ username: 'lin', password: 'longenough', displayName: '' })
    })).resolves.toEqual({ ok: true })

    expect(fetchMock).toHaveBeenCalledOnce()
  })
})

/**
 * 会话失效信号。
 *
 * 守卫接口回 401 说明这次会话已经不作数了；界面据此回登录页并停止空转。判据必须窄：
 * 「凭据对了没有」的失败（登录接口的 401）不是会话失效，其它错误码也不是。
 */
describe('会话失效信号', () => {
  function failing(status: number, code: string) {
    vi.stubGlobal('fetch', vi.fn(async () =>
      new Response(JSON.stringify({ code, message: '登录已失效，请重新登录' }), { status })))
  }

  it('守卫接口回 401 时广播一次', async () => {
    const { events } = windowRecording()
    failing(401, 'UNAUTHORIZED')
    try {
      await expect(request('/api/web/scene')).rejects.toThrow('登录已失效')
      expect(events).toHaveLength(1)
    } finally { vi.unstubAllGlobals() }
  })

  it('登录接口的 401 不算——那是「这次凭据不对」，不是会话在半路失效', async () => {
    const { events } = windowRecording()
    failing(401, 'INVALID_CREDENTIALS')
    try {
      await expect(request('/api/auth/login', { method: 'POST', body: '{}' })).rejects.toThrow('登录已失效')
      expect(events).toHaveLength(0)
    } finally { vi.unstubAllGlobals() }
  })

  it('别的失败（5xx、409）不广播', async () => {
    const { events } = windowRecording()
    failing(500, 'SCENE_READ_FAILED')
    try {
      await expect(request('/api/web/scene')).rejects.toThrow('登录已失效')
      expect(events).toHaveLength(0)
    } finally { vi.unstubAllGlobals() }
  })
})
