import { createRoot } from 'react-dom/client'
import { CanvasApp } from './CanvasApp'
import type { CanvasBridgeTransport, Envelope } from './Protocol/VersionedMessages'

type WebViewWindow = Window & {
  chrome?: { webview?: { postMessage: (message: unknown) => void; addEventListener: (type: string, listener: (event: MessageEvent) => void) => void; removeEventListener: (type: string, listener: (event: MessageEvent) => void) => void } }
}

function createWebSocketTransport(wsUrl: string): CanvasBridgeTransport {
  let ws: WebSocket | null = null
  const handlers = new Set<(message: unknown) => void>()
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null

  function connect() {
    ws = new WebSocket(wsUrl)
    ws.onmessage = (event) => {
      try {
        const data = JSON.parse(event.data)
        handlers.forEach((h) => h(data))
      } catch { /* ignore malformed messages */ }
    }
    ws.onclose = () => {
      ws = null
      reconnectTimer = setTimeout(connect, 2000)
    }
    ws.onerror = () => { ws?.close() }
  }

  connect()

  return {
    send: (message: Envelope) => {
      if (ws?.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify(message))
      }
    },
    subscribe: (handler) => {
      handlers.add(handler)
      return () => { handlers.delete(handler) }
    }
  }
}

function createWebViewTransport(): CanvasBridgeTransport {
  return {
    send: (message: Envelope) => {
      const webview = (window as WebViewWindow).chrome?.webview
      if (webview) webview.postMessage(message)
      else window.parent.postMessage(message, '*')
    },
    subscribe: (handler) => {
      const listener = (event: MessageEvent) => handler(event.data)
      const webview = (window as WebViewWindow).chrome?.webview
      if (webview) {
        webview.addEventListener('message', listener)
        return () => webview.removeEventListener('message', listener)
      }
      window.addEventListener('message', listener)
      return () => window.removeEventListener('message', listener)
    }
  }
}

const isInWebView = typeof (window as WebViewWindow).chrome?.webview !== 'undefined'
const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
const wsUrl = `${wsProtocol}//${window.location.host}/ws/canvas`

const transport = isInWebView ? createWebViewTransport() : createWebSocketTransport(wsUrl)

createRoot(document.getElementById('root')!).render(<CanvasApp transport={transport} />)
