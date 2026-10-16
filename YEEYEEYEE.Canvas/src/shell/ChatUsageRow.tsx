import type { ChatMessage } from './chatStream'

export function ChatUsageRow({ message }: { message: ChatMessage }) {
  const report = message.usageReport
  const hasTokens = !!message.usage && (message.usage.inputTokens > 0 || message.usage.completionTokens > 0)
  const waiting = message.state === 'streaming'
  const contextPercent = message.contextWindow && message.contextWindow > 0 && message.usage && message.usage.inputTokens > 0
    ? `${(message.usage.inputTokens * 100 / message.contextWindow).toFixed(1).replace(/\.0$/, '')}%`
    : null
  return <details className="df-agent-usage-details">
    <summary aria-label="查看这一轮用量明细" title="点开看这一轮的用量明细">
      {report?.chips.map((chip, index) => <span key={index} className="df-agent-usage">{chip}{chip.startsWith('上下文 ') && contextPercent ? ` · ${contextPercent}` : ''}</span>)}
      {!hasTokens && <span className="df-agent-usage-note">{waiting ? '用量：等待接口回传…' : '用量：—（这一家接口没有回传 token 用量）'}</span>}
    </summary>
    <div className="df-section">
      <strong>这一轮 · {message.model}</strong>
      {hasTokens || !waiting ? report?.details.map((line, index) => <span key={index}>{line}</span>) : <span>正在生成，最终用量尚未返回。</span>}
      {!message.contextWindow && <span>模型未声明上下文窗口，无法计算占比。</span>}
      {hasTokens && message.usage!.cacheHitTokens + message.usage!.cacheMissTokens === 0 && <span>接口未回传缓存用量，无法计算缓存命中率。</span>}
      {message.decodeSeconds == null && <span>尚未测得有效解码窗口，无法计算 tok/s。</span>}
      {message.firstTokenSeconds == null && <span>尚未测得首字延迟。</span>}
    </div>
  </details>
}
