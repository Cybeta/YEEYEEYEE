import { createRoot } from 'react-dom/client'
import { WebCanvasApp } from './WebCanvasApp'

/**
 * 唯一入口：网页端工作台。
 *
 * 第 181 轮之前这里还有一条分支：页面跑在 WebView2 里（`window.chrome.webview` 存在）时渲染
 * 另一份 `CanvasApp`，走 `postMessage` 与宿主对话。那条路随旧的 HostBridge 兼容面一起删了——
 * 仓库里没有任何 WebView 宿主（整个 `YEEYEEYEE.Desktop.Avalonia` 里搜不到 WebView），
 * 那条分支**永远进不去**，却让每次构建都多打进一份完整的前端。
 */
createRoot(document.getElementById('root')!).render(<WebCanvasApp />)
