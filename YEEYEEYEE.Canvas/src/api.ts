/**
 * 网页端唯一的 HTTP 入口。
 *
 * 会话 cookie 由浏览器自动带上，所以这里**不手动塞 Authorization 头**：凭据在 HttpOnly cookie 里，
 * 脚本读不到它，也就不存在「前端把令牌存哪儿」这个问题。
 *
 * 抽成独立模块是因为它从 WebCanvasApp 里长出来了：编辑锁也会发请求，
 * 而两处各写一份 fetch 的差别会在「错误码怎么拼」这种地方慢慢分叉。
 */

export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : '未知错误'
}

export async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...options,
    credentials: 'same-origin',
    headers: { ...(options?.body ? { 'Content-Type': 'application/json' } : {}) },
    cache: 'no-store'
  })
  const data: unknown = await response.json().catch(() => null)
  if (!response.ok) {
    const failure = data && typeof data === 'object' ? data as { code?: unknown; message?: unknown } : {}
    throw new Error(`${typeof failure.code === 'string' ? `[${failure.code}] ` : ''}${typeof failure.message === 'string' ? failure.message : `HTTP ${response.status}`}`)
  }
  return data as T
}
