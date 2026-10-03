import type { ReactNode } from 'react'

import { uiText } from './uiText'

/**
 * 工作台外壳：桌面端 MainWindow.axaml 的网页端镜像。
 *
 * 搬的是**布局与层级**，不是行为：
 *   WorkbenchRoot  Margin 10 / Cols 244,*,336 / Rows 58,*,34
 *   ├─ 顶部命令条（跨三列）
 *   ├─ 左指挥舱（跨第 2、3 行）
 *   ├─ 中央画布区
 *   ├─ 右侧栏（检查器 / Agent 二选一）
 *   └─ 底部状态条（跨中央与右侧两列）
 *
 * 桌面端自己的窗口按钮（最小化/最大化/关闭）与那圈拖拽缩放抓手**故意不搬**：
 * 浏览器已经有窗口边框了，再画一套假的只会让人去点它。
 *
 * 这个组件只负责摆位置，不认识画布数据——三个数据区（工作树、中央区、右侧栏）都是插槽，
 * 由 WebCanvasApp 传进来。这样外壳就不会悄悄长成一个什么都知道的上帝组件。
 */

export type WorkbenchView = 'canvas' | 'timeline' | 'script'
export type DockMode = 'inspector' | 'agent' | 'settings'
export type RailSection = 'project' | 'story'
export type SyncTone = 'ok' | 'busy' | 'error' | 'idle' | 'stale'

export type StatusFacts = {
  text: string
  revision: string
  syncLabel: string
  syncTone: SyncTone
  /** 状态条上的动作（现在只用在「有新修订」时引导重新加载）。没有它就没有按钮。 */
  syncAction?: { label: string; onClick: () => void }
  nodes: string
  edges: string
  canvasSize: string
  version: string
  saved: string
}

export type WorkbenchChrome = {
  version: string
  projectName: string
  /** 项目卡的副标题。桌面端这里放的是项目路径——网页端**故意不放**：
   *  那会把服务端的目录结构泄露给浏览器。改成对这个用户真正有用的两件事：修订号与可写性。 */
  projectSubtitle: string
  canvasTitle: string
  view: WorkbenchView
  onView: (view: WorkbenchView) => void
  section: RailSection
  onSection: (section: RailSection) => void
  search: string
  onSearch: (value: string) => void
  dockOpen: boolean
  onToggleDock: () => void
  agentActive: boolean
  onOpenAgent: () => void
  onSave: () => void
  saveLabel: string
  saveDisabled: boolean
}

export type WorkbenchShellProps = {
  chrome: WorkbenchChrome
  status: StatusFacts
  session: ReactNode
  tree: ReactNode
  workspace: ReactNode
  dock: ReactNode
  badge: { visible: boolean; text: string; onClick: () => void }
}

// 这几条标签两端各有一份（桌面端是 MainWindow.axaml 里那几个 Content），所以措辞走共享文案：
// 一端改了名字，另一端不会再悄悄留着旧名字。
const VIEWS: Array<{ key: WorkbenchView; label: string }> = [
  { key: 'canvas', label: uiText('panel.canvas') },
  { key: 'timeline', label: uiText('panel.timeline') },
  { key: 'script', label: uiText('panel.script') }
]

const SECTIONS: Array<{ key: RailSection; label: string }> = [
  { key: 'project', label: uiText('panel.projectTree') },
  { key: 'story', label: uiText('panel.storyCanvas') }
]

/** 同步指示点的颜色：这几档是状态编码，不是装饰。 */
const SYNC_COLORS: Record<SyncTone, string> = {
  ok: 'var(--df-primary)',
  busy: 'var(--df-warning)',
  error: 'var(--df-error)',
  idle: 'var(--df-ink-3)',
  // 「别人改了」不是错，是提醒：用琥珀色，跟「正在忙」同一个色，但文案把差别说清楚。
  stale: 'var(--df-warning)'
}

export function WorkbenchShell({ chrome, status, session, tree, workspace, dock, badge }: WorkbenchShellProps) {
  return (
    <div className={`df-workbench${chrome.dockOpen ? '' : ' is-dock-closed'}`}>
      <div className="df-aurora" aria-hidden="true" />

      <header className="df-glass df-row-top df-col-all">
        <div className="df-edge-top" />
        <div className="df-titlebar">
          <div className="df-brand">
            <span className="df-brand-name">YEEYEEYEE</span>
            <span className="df-dim">{chrome.version}</span>
          </div>

          <div className="df-breadcrumb">
            <span className="df-muted df-ellipsis df-breadcrumb-text" title={chrome.projectSubtitle}>{chrome.projectName}</span>
            <span className="df-dim">›</span>
            <span className="df-heading df-ellipsis df-breadcrumb-text" title={chrome.canvasTitle}>{chrome.canvasTitle}</span>
          </div>

          <div className="df-seg-group" role="tablist" aria-label="视图">
            {VIEWS.map((view) => (
              <button
                key={view.key}
                type="button"
                role="tab"
                aria-selected={chrome.view === view.key}
                className={`df-seg${chrome.view === view.key ? ' is-active' : ''}`}
                onClick={() => chrome.onView(view.key)}
              >
                {view.label}
              </button>
            ))}
          </div>

          <div className="df-actions">
            <label className="df-field df-search">
              <span className="df-tool-button" aria-hidden="true" style={{ padding: '4px 2px' }}>⌕</span>
              <input
                className="df-input"
                value={chrome.search}
                onChange={(event) => chrome.onSearch(event.target.value)}
                placeholder="搜索节点、工作树、设定库"
                aria-label="搜索"
              />
            </label>
            {/* 撤销/重做与「生成章节工作树」在服务端还没有对应接口，做成可见但不可点，
                而不是点了没反应——点了没反应比灰着更让人困惑。 */}
            <button type="button" className="df-tool-button" disabled title="网页端尚未接入撤销">↶</button>
            <button type="button" className="df-tool-button" disabled title="网页端尚未接入重做">↷</button>
            <button
              type="button"
              className={`df-tool-button${chrome.dockOpen ? ' is-active' : ''}`}
              onClick={chrome.onToggleDock}
              title="检查器"
            >◧</button>
            <button type="button" className="df-primary-button" disabled title="生成章节工作树在网页端尚未接入">
              生成章节工作树
            </button>
            <button
              type="button"
              className="df-tool-button"
              onClick={chrome.onSave}
              disabled={chrome.saveDisabled}
              title={chrome.saveDisabled ? '没有待保存的修改' : '把检查器里的修改写回画布'}
            >{chrome.saveLabel}</button>
            {session}
          </div>
        </div>
      </header>

      <aside className="df-glass df-span-bottom df-col-rail">
        <div className="df-edge-top" />
        <div className="df-rail-body">
          <button type="button" className="df-nav-button" style={{ background: 'var(--df-surface-2)', borderColor: 'var(--df-line)', padding: '10px 12px' }} disabled title="项目切换在网页端尚未接入">
            <div className="df-row-between">
              <span className="df-stack" style={{ alignItems: 'flex-start' }}>
                <span className="df-heading" style={{ fontSize: 12 }}>{chrome.projectName}</span>
                <span className="df-ellipsis" style={{ fontSize: 11, color: 'var(--df-ink-2)' }}>{chrome.projectSubtitle}</span>
              </span>
              <span className="df-dim">›</span>
            </div>
          </button>

          <div className="df-grid-2" style={{ gridTemplateColumns: '1fr 1fr 1fr', gap: 6, marginTop: 8 }}>
            <button type="button" className="df-mini-button" disabled title="新建项目在网页端尚未接入">新建项目</button>
            <button type="button" className="df-mini-button" disabled title="打开项目在网页端尚未接入">打开项目</button>
            <button type="button" className="df-mini-button" disabled title="新建画布在网页端尚未接入">新建画布</button>
          </div>

          <div className="df-section-label" style={{ margin: '16px 0 8px 10px' }}>工 作 区</div>
          <div className="df-stack" style={{ gap: 4 }}>
            {SECTIONS.map((section) => (
              <button
                key={section.key}
                type="button"
                className={`df-nav-button${chrome.section === section.key ? ' is-active' : ''}`}
                onClick={() => chrome.onSection(section.key)}
              >
                {section.label}
              </button>
            ))}
            <button
              type="button"
              className={`df-nav-button${chrome.agentActive ? ' is-active' : ''}`}
              onClick={chrome.onOpenAgent}
            >
              Agent 协作
            </button>
          </div>

          <div className="df-section-label" style={{ margin: '16px 0 8px 10px' }}>工 作 树 资 源</div>
          <div className="df-card df-grow" style={{ padding: 8, overflow: 'auto' }}>
            {tree}
          </div>

          <button type="button" className="df-nav-button" style={{ marginTop: 10 }} disabled title="设置页（模型接入 / 生图生视频 / 技能管理）在网页端尚未接入">
            <span className="df-row"><span style={{ color: 'var(--df-primary)' }}>⚙</span><span style={{ color: 'var(--df-ink)' }}>设置</span></span>
          </button>
          <div className="df-dim" style={{ margin: '12px 0 0 4px', fontSize: 11 }}>{chrome.version} · 服务端项目</div>
        </div>
      </aside>

      <section className="df-glass-flat df-row-1 df-col-center" style={{ marginRight: chrome.dockOpen ? 10 : 0, marginBottom: 10 }}>
        {workspace}
      </section>

      <div className="df-row-1 df-col-dock" style={{ marginBottom: 10 }}>
        {dock}
      </div>

      <footer className="df-glass df-row-bottom df-col-center-wide">
        <div className="df-edge-top" />
        <div className="df-statusbar-inner">
          <span className="df-muted df-ellipsis">{status.text}</span>
          <span className="df-dim">{status.revision}</span>
          <span className="df-sync" style={{ color: SYNC_COLORS[status.syncTone] }}>
            <span aria-hidden="true">●</span>
            <span>{status.syncLabel}</span>
          </span>
          {status.syncAction && (
            <button type="button" className="df-mini-button" onClick={status.syncAction.onClick}>
              {status.syncAction.label}
            </button>
          )}
          <div className="df-statusbar-right">
            <span className="df-dim">{status.nodes}</span>
            <span className="df-dim" title={status.edges}>{status.edges}</span>
            <span className="df-dim">{status.canvasSize}</span>
            <span className="df-dim">{status.version}</span>
            <span className="df-dim">{status.saved}</span>
          </div>
        </div>
      </footer>

      {badge.visible && (
        <button type="button" className="df-agent-badge" onClick={badge.onClick} title="打开 Agent 协作面板">
          <span className="df-agent-badge-ring">{badge.text}</span>
        </button>
      )}
    </div>
  )
}
