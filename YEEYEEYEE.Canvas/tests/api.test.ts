import { describe, expect, it, vi } from 'vitest'
import { request } from '../src/api'

describe('HTTP request headers', () => {
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
