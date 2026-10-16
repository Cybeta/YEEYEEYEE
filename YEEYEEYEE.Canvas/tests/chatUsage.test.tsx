import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { ChatUsageRow } from '../src/shell/ChatUsageRow'
import { readChatStream, type ChatMessage } from '../src/shell/chatStream'

const message: ChatMessage = { id: 'reply', role: 'assistant', content: '', thinking: '', state: 'completed', model: 'test' }
function render(overrides: Partial<ChatMessage>) { return renderToStaticMarkup(createElement(ChatUsageRow, { message: { ...message, ...overrides } })) }

describe('chat usage', () => {
  it('renders shared report chips and accessible expandable details', () => {
    const html = render({ usage: { inputTokens: 2000, completionTokens: 100, cacheHitTokens: 500, cacheMissTokens: 1500 }, contextWindow: 10000, firstTokenSeconds: 1.5, decodeSeconds: 4,
      usageReport: { chips: ['↑2K ↓100', '缓存 25%', '上下文 2K/10K', '25 tok/s', '首字 1.5s'], details: ['命中 ÷ 计费输入', '第一个字到最后一个字'], hasAnything: true, summary: '' } })
    expect(html).toContain('<details')
    expect(html).toContain('查看这一轮用量明细')
    for (const text of ['↑2K ↓100', '缓存 25%', '上下文 2K/10K', '25 tok/s', '首字 1.5s', '命中 ÷ 计费输入']) expect(html).toContain(text)
    expect(html).toContain('上下文 2K/10K · 20%')
    expect(html).not.toContain('没有回传 token')
  })
  it('distinguishes missing usage from zero even when first-token timing exists', () => {
    const html = render({ firstTokenSeconds: 1.2, usageReport: { chips: ['首字 1.2s'], details: ['不是 0，是没有。'], hasAnything: true, summary: '' } })
    expect(html).toContain('没有回传 token 用量')
    expect(html).toContain('首字 1.2s')
    expect(html).toContain('不是 0，是没有')
    expect(html).not.toContain('↑0')
  })
  it('waits before concluding provider usage is missing and explains unavailable metadata', () => {
    const waiting = render({ state: 'streaming' })
    expect(waiting).toContain('等待接口回传')
    expect(waiting).not.toContain('没有回传 token')
    const restored = render({ usage: { inputTokens: 1200, completionTokens: 80, cacheHitTokens: 0, cacheMissTokens: 0 }, usageReport: { chips: ['↑1.2K ↓80'], details: [], hasAnything: true, summary: '' } })
    for (const text of ['模型未声明上下文窗口', '接口未回传缓存用量', '无法计算 tok/s', '尚未测得首字延迟']) expect(restored).toContain(text)
  })
  it('preserves extended usage and restored report across fragmented SSE frames', async () => {
    const usage = { usage: { inputTokens: 2000, completionTokens: 100, cacheHitTokens: 500, cacheMissTokens: 1500 }, contextWindow: 10000, firstTokenSeconds: 1.5, decodeSeconds: 4, usageReport: { chips: ['25 tok/s'], details: ['解码窗口 4 秒'], hasAnything: true, summary: '25 tok/s' } }
    const session = { id: 'session', messages: [{ ...message, ...usage }] }
    const bytes = new TextEncoder().encode(`event: usage\ndata: ${JSON.stringify(usage)}\n\nevent: done\ndata: ${JSON.stringify(session)}\n\n`)
    const stream = new ReadableStream<Uint8Array>({ start(controller) { for (let i = 0; i < bytes.length; i += 3) controller.enqueue(bytes.slice(i, i + 3)); controller.close() } })
    const events: unknown[] = []
    await readChatStream(new Response(stream), event => events.push(event))
    expect(events).toEqual([{ type: 'usage', data: usage }, { type: 'done', data: session }])
  })
})
