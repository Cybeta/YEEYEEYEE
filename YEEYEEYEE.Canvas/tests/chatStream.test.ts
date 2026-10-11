import { describe, expect, it } from 'vitest'
import { readChatStream } from '../src/shell/chatStream'

describe('chat stream', () => {
  it('preserves fragmented Chinese UTF-8, thinking, text, usage and terminal snapshot', async () => {
    const bytes = new TextEncoder().encode('event: thinking\ndata: {"text":"思考"}\n\nevent: text\ndata: {"text":"正文"}\n\nevent: usage\ndata: {"inputTokens":7}\n\nevent: done\ndata: {"id":"session"}\n\n')
    const stream = new ReadableStream<Uint8Array>({ start(controller) { for (let i = 0; i < bytes.length; i += 2) controller.enqueue(bytes.slice(i, i + 2)); controller.close() } })
    const events: unknown[] = []
    await readChatStream(new Response(stream), event => events.push(event))
    expect(events).toEqual([{ type: 'thinking', data: { text: '思考' } }, { type: 'text', data: { text: '正文' } }, { type: 'usage', data: { inputTokens: 7 } }, { type: 'done', data: { id: 'session' } }])
  })
  it('does not report a truncated stream as success', async () => {
    await expect(readChatStream(new Response('event: text\ndata: {"text":"partial"}\n\n'), () => {})).rejects.toThrow('连接中断')
  })
  it('reports backend failure before streaming', async () => {
    await expect(readChatStream(new Response('{"message":"模型不可用"}', { status: 503 }), () => {})).rejects.toThrow('模型不可用')
  })
})
