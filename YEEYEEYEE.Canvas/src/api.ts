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

/**
 * 带上错误码与原始响应体的失败。
 *
 * 光有一句 message 不够：编辑锁的 409 把**持有者是谁**放在响应体里，
 * 而「谁在编辑」正是要做成界面文案的东西——只留一句话就没法显示「等陈默保存」。
 */
export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly body: unknown

  constructor(message: string, status: number, code: string, body: unknown) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.body = body
  }
}

/** 失败的业务码（认不出就回空串，调用方按「未知失败」处理）。 */
export function apiCode(error: unknown): string {
  return error instanceof ApiError ? error.code : ''
}

/** 失败的原始响应体。 */
export function apiBody(error: unknown): unknown {
  return error instanceof ApiError ? error.body : null
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
    const code = typeof failure.code === 'string' ? failure.code : ''
    const message = typeof failure.message === 'string' ? failure.message : `HTTP ${response.status}`
    throw new ApiError(`${code ? `[${code}] ` : ''}${message}`, response.status, code, data)
  }
  return data as T
}
