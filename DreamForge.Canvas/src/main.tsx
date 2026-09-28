import { createRoot } from 'react-dom/client'
import { CanvasApp } from './CanvasApp'
import type { CanvasBridgeTransport, Envelope } from './Protocol/VersionedMessages'

type WebViewWindow = Window & {
  chrome?: { webview?: { postMessage: (message: unknown) => void; addEventListener: (type: string, listener: (event: MessageEvent) => void) => void; removeEventListener: (type: string, listener: (event: MessageEvent) => void) => void } }
}

const transport: CanvasBridgeTransport = {
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
createRoot(document.getElementById('root')!).render(<CanvasApp transport={transport} />)
