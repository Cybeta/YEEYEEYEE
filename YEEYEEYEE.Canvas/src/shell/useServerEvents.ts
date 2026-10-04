import { useEffect, useRef, useState } from 'react'
import { parseServerEvent, type CanvasChangedEvent, type EditsChangedEvent, type PresenceChangedEvent } from './serverEvents'

/**
 * 订阅服务端的变更推送（SSE）。
 *
 * 断线不自建重连逻辑：`EventSource` 会按服务端给的 `retry` 自己回来。
 * 这里只如实记下「现在没连上」——因为轮询仍然在兜底，推送断了是**降级**而不是故障，
 * 把它渲染成红色错误反而会让人以为画布坏了。
 */
export type ServerEventsState = { connected: boolean; error: string }

export function useServerEvents(options: {
  enabled: boolean
  onCanvasChanged?: (event: CanvasChangedEvent) => void
  onEditsChanged?: (event: EditsChangedEvent) => void
  onPresenceChanged?: (event: PresenceChangedEvent) => void
}): ServerEventsState {
  const [connected, setConnected] = useState(false)
  const [error, setError] = useState('')
  // 回调放 ref：它们每次渲染都是新函数，进依赖数组会让订阅每次渲染都重开一条连接。
  const handlers = useRef(options)
  handlers.current = options

  useEffect(() => {
    if (!options.enabled || typeof EventSource === 'undefined') return
    const source = new EventSource('/api/web/events')

    source.onopen = () => { setConnected(true); setError('') }
    source.onerror = () => {
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
      setConnected(false)
    }
  }, [options.enabled])

  return { connected, error }
}
