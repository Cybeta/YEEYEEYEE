import { request } from '../api'

export type WorkflowImportResult = {
  converted: number; failed: number;
  workflows: { key: string; title: string; converted: boolean; note: string; error: string }[]
}
type Session = { id: string; requestId: string; url: string; origin: string }

export function startWorkflowConversion(paths: string[], baseUrl: string, timeoutMs = 900000) {
  const popup = window.open('about:blank', `comfyui-${crypto.randomUUID()}`)
  if (!popup) throw new Error('转换窗口被浏览器拦截，请允许弹出窗口后重试。')
  let session: Session | undefined
  let active = true
  let completing = false
  let resolve!: (value: WorkflowImportResult) => void
  let reject!: (error: Error) => void
  const result = new Promise<WorkflowImportResult>((done, fail) => { resolve = done; reject = fail })
  const controller = new AbortController()
  const endpoint = (id: string) => `/api/web/settings/workflow-conversions/${encodeURIComponent(id)}`
  const release = (id: string) => { void request(endpoint(id), { method: 'DELETE', keepalive: true }).catch(() => {}) }
  function cleanup() {
    window.removeEventListener('message', receive)
    window.clearInterval(closedTimer)
    window.clearTimeout(timeout)
    popup?.close()
  }
  function cancel(message = '已取消工作流转换。') {
    if (!active) return
    active = false
    controller.abort()
    cleanup()
    if (session) release(session.id)
    reject(new Error(message))
  }
  function receive(event: MessageEvent) {
    if (!active || completing || !session || event.origin !== session.origin || event.source !== popup) return
    const data: unknown = event.data
    if (!data || typeof data !== 'object') return
    const message = data as Record<string, unknown>
    if (message.kind !== 'comfyui-conversion' || message.session !== session.id || message.requestId !== session.requestId || typeof message.success !== 'boolean') return
    if (!message.success) { cancel(typeof message.error === 'string' ? message.error : '官方前端转换失败。'); return }
    completing = true
    window.removeEventListener('message', receive)
    window.clearInterval(closedTimer)
    void request<WorkflowImportResult>(`${endpoint(session.id)}/complete`, { method: 'POST', signal: controller.signal }).then(value => {
      if (!active) return
      active = false
      cleanup()
      resolve(value)
    }).catch(error => cancel(error instanceof Error ? error.message : '完成导入失败。'))
  }
  window.addEventListener('message', receive)
  const closedTimer = window.setInterval(() => { if (popup.closed) cancel('转换窗口已关闭，导入已取消。') }, 500)
  const timeout = window.setTimeout(() => cancel('工作流转换超时，请重试。'), timeoutMs)
  void request<Session>('/api/web/settings/workflow-conversions', { method: 'POST', body: JSON.stringify({ paths: [...paths], baseUrl }) }).then(value => {
    session = value
    if (!active) { release(value.id); return }
    const url = new URL(value.url)
    if (!['https:', 'http:'].includes(url.protocol) || url.origin !== value.origin || value.origin === 'null') throw new Error('转换窗口来源无效。')
    popup.location.href = url.href
  }).catch(error => cancel(error instanceof Error ? error.message : '创建转换会话失败。'))
  return { result, cancel }
}
