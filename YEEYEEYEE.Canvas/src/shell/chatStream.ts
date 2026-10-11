export type ChatUsage = { inputTokens: number; completionTokens: number; cacheHitTokens: number; cacheMissTokens: number }
export type ChatMessage = { id: string; role: string; attachmentIds?: string[]; content: string; thinking: string; state: string; error?: string; usage?: ChatUsage; model: string; mode?: string; actionState?: string; actions?: { kind: string; title: string; target: string; reason: string }[]; ask?: { question: string; options: string[] }; hints?: string[]; actionErrors?: string[]; skillActions?: { kind: string; target: string; content: string; reason: string }[]; skillState?: string; skillError?: string; skillJobIds?: string[] }
export type ChatSession = { id: string; title: string; updatedAt: string; messages: ChatMessage[]; jobIds?: string[] }
export type ChatEvent = { type: string; data: unknown }

// 保留跨网络块的UTF-8与SSE帧，不能把每次read当作一整条事件。
export async function readChatStream(response: Response, onEvent: (event: ChatEvent) => void): Promise<void> {
  if (!response.ok) {
    const failure = await response.json().catch(() => null) as { message?: string } | null
    throw new Error(failure?.message ?? `HTTP ${response.status}`)
  }
  if (!response.body) throw new Error('聊天流没有响应体')
  const reader = response.body.getReader(), decoder = new TextDecoder()
  let buffer = '', doneSeen = false
  try {
    for (;;) {
      const chunk = await reader.read()
      buffer += decoder.decode(chunk.value, { stream: !chunk.done })
      buffer = buffer.replace(/\r\n/g, '\n')
      let boundary: number
      while ((boundary = buffer.indexOf('\n\n')) >= 0) {
        const frame = buffer.slice(0, boundary); buffer = buffer.slice(boundary + 2)
        const lines = frame.split('\n')
        const type = lines.find(line => line.startsWith('event:'))?.slice(6).trim()
        const data = lines.filter(line => line.startsWith('data:')).map(line => line.slice(5).trimStart()).join('\n')
        if (type && data) { onEvent({ type, data: JSON.parse(data) }); if (type === 'done') doneSeen = true }
      }
      if (chunk.done) break
    }
    if (!doneSeen) throw new Error('连接中断，回复状态将从服务端恢复')
  } finally { await reader.cancel().catch(() => {}); reader.releaseLock() }
}
