import { useEffect, useRef, useState } from 'react'
import { canvasUrl } from '../api'
import { parseServerEvent, type CanvasChangedEvent, type EditsChangedEvent, type PresenceChangedEvent } from './serverEvents'

/**
 * 订阅服务端的变更推送（SSE）。
 *
 * 断线不自建重连逻辑：`EventSource` 会按服务端给的 `retry` 自己回来。
 * 这里只如实记下「现在没连上」——因为轮询仍然在兜底，推送断了是**降级**而不是故障，
 * 把它渲染成红色错误反而会让人以为画布坏了。
 *
 * 要留意一件事：这条流**不是可靠投递、也不重放**。断线期间服务端推过的事件就此消失
 * （它只说「变了」，本身不存档，真内容仍然靠 GET 取）。所以自动重连只保证「之后的新推送接得上」，
 * 断线期间那段空档必须由 `onReconnected` 的调用方**自己重新取一次快照**补上。
 */
export type ServerEventsState = { connected: boolean; error: string }

export function useServerEvents(options: {
  enabled: boolean
  onCanvasChanged?: (event: CanvasChangedEvent) => void
  onEditsChanged?: (event: EditsChangedEvent) => void
  onPresenceChanged?: (event: PresenceChangedEvent) => void
  /**
   * 断线之后重新连上时调一次（首次连上不算——那时调用方刚取过数据，再取一次是白跑）。
   *
   * 这就是断线恢复的**唯一入口**：SSE 不重放，重连之后必须自己去拉一次快照，
   * 否则断线期间别人的改动会一直看不到，而界面上连「有变动」都不会出现。具体怎么补由调用方定。
   */
  onReconnected?: () => void
}): ServerEventsState {
  const [connected, setConnected] = useState(false)
  const [error, setError] = useState('')
  // 回调放 ref：它们每次渲染都是新函数，进依赖数组会让订阅每次渲染都重开一条连接。
  const handlers = useRef(options)
  handlers.current = options

  useEffect(() => {
    if (!options.enabled || typeof EventSource === 'undefined') return
    const source = new EventSource(canvasUrl('/api/web/events'))

    // 断过一次才算「重连」：首次连上时调用方刚取过数据，那时再补一次是白跑。
    let interrupted = false
    source.onopen = () => {
      setConnected(true)
      setError('')
      if (interrupted) { interrupted = false; handlers.current.onReconnected?.() }
    }
    source.onerror = () => {
      interrupted = true
      setConnected(false)
      setError('推送连接断开，正在重连（编辑锁仍按 15 秒轮询兜底）')
    }

    const onCanvas = (raw: Event) => {
      const event = parseServerEvent('canvas.changed', (raw as MessageEvent<string>).data ?? '')
      if (event?.type === 'canvas.changed') handlers.current.onCanvasChanged?.(event)
    }
    const onEdits = (raw: Event) => {
      const event = parseServerEvent('edits.changed', (raw as MessageEvent<string>).data ?? '')
      if (event?.type === 'edits.changed') handlers.current.onEditsChanged?.(event)
    }
    const onPresence = (raw: Event) => {
      const event = parseServerEvent('presence.changed', (raw as MessageEvent<string>).data ?? '')
      if (event?.type === 'presence.changed') handlers.current.onPresenceChanged?.(event)
    }

    // 具名事件：服务端写 `event: canvas.changed`，这里按名字分别接。一条 JSON 也能干这事，
    // 但具名事件让浏览器开发者工具里一眼能看出来了什么，也让客户端不必先解析再分派。
    source.addEventListener('canvas.changed', onCanvas)
    source.addEventListener('edits.changed', onEdits)
    source.addEventListener('presence.changed', onPresence)

    return () => {
      source.removeEventListener('canvas.changed', onCanvas)
      source.removeEventListener('edits.changed', onEdits)
      source.removeEventListener('presence.changed', onPresence)
      source.close()
      // 连错误一起清掉：关掉订阅（退出登录、换画布）之后那条断线原因已经不作数了，
      // 留着它会让下一次登录后的界面先显示一句「推送连接断开」。
      setConnected(false)
      setError('')
    }
  }, [options.enabled])

  return { connected, error }
}
