import type { ReactNode } from 'react'
import { uiText } from './uiText'
import type { DockMode } from './WorkbenchShell'

/**
 * 右侧栏外框：标题 + 页签（节点详情 / Agent 协作 / 设置）+ 内容插槽。
 *
 * 桌面端前两块面板**共用同一个格子**，二选一显示（MainWindow.axaml 里
 * InspectorPanel 与 AgentPanel 是同一个 Panel 下的两个兄弟）。这里保留同样的关系：
 * 页签切换的是同一格里装谁，不是并排多开一栏。
 *
 * 前两个页签的词走共享文案（桌面端那两处叫同一个名字）；「设置」只是网页端的第三格
 * ——桌面端的设置是**另一扇窗口**，不是这个栏里的页签，所以那个词不进共享文件。
 */
export function RightDock({ mode, onMode, onClose, children }: {
  mode: DockMode
  onMode: (mode: DockMode) => void
  onClose: () => void
  children: ReactNode
}) {
  const title = mode === 'inspector' ? uiText('dock.inspector') : mode === 'agent' ? uiText('dock.agent') : '设置'
  return (
    <div className="df-glass" style={{ height: '100%' }}>
      <div className="df-edge-top" />
      <div className="df-panel">
        <div className="df-panel-head">
          <span className="df-heading" style={{ fontSize: 13 }}>{title}</span>
          <span className="df-row" style={{ gap: 2 }}>
            <button type="button" className="df-tool-button" onClick={onClose} title="收起右侧栏">×</button>
            <button type="button" className="df-tool-button" disabled title="面板菜单在网页端尚未接入">•••</button>
          </span>
        </div>
        <div className="df-panel-tabs">
          <button type="button" className={`df-tab${mode === 'inspector' ? ' is-active' : ''}`} onClick={() => onMode('inspector')}>
            {uiText('dock.inspector')}
          </button>
          <button type="button" className={`df-tab${mode === 'agent' ? ' is-active' : ''}`} onClick={() => onMode('agent')}>
            {uiText('dock.agent')} ›
          </button>
          <button type="button" className={`df-tab${mode === 'settings' ? ' is-active' : ''}`} onClick={() => onMode('settings')}>
            设置
          </button>
        </div>
        <div className="df-panel-body df-scroll">{children}</div>
      </div>
    </div>
  )
}
