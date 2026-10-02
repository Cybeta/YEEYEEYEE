import type { ReactNode } from 'react'
import type { DockMode } from './WorkbenchShell'

/**
 * 右侧栏外框：标题 + 「节点详情 / Agent 协作」两个页签 + 内容插槽。
 *
 * 桌面端这两块面板**共用同一个格子**，二选一显示（MainWindow.axaml 里
 * InspectorPanel 与 AgentPanel 是同一个 Panel 下的两个兄弟）。这里保留同样的关系：
 * 页签切换的是同一格里装谁，不是并排多开一栏。
 */
export function RightDock({ mode, onMode, onClose, children }: {
  mode: DockMode
  onMode: (mode: DockMode) => void
  onClose: () => void
  children: ReactNode
}) {
  return (
    <div className="df-glass" style={{ height: '100%' }}>
      <div className="df-edge-top" />
      <div className="df-panel">
        <div className="df-panel-head">
          <span className="df-heading" style={{ fontSize: 13 }}>节点检查器</span>
          <span className="df-row" style={{ gap: 2 }}>
            <button type="button" className="df-tool-button" onClick={onClose} title="收起右侧栏">×</button>
            <button type="button" className="df-tool-button" disabled title="面板菜单在网页端尚未接入">•••</button>
          </span>
        </div>
        <div className="df-panel-tabs">
          <button type="button" className={`df-tab${mode === 'inspector' ? ' is-active' : ''}`} onClick={() => onMode('inspector')}>
            节点详情
          </button>
          <button type="button" className={`df-tab${mode === 'agent' ? ' is-active' : ''}`} onClick={() => onMode('agent')}>
            Agent 协作 ›
          </button>
        </div>
        <div className="df-panel-body df-scroll">{children}</div>
      </div>
    </div>
  )
}
