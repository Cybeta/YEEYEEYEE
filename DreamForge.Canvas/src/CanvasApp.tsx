import { useEffect, useMemo, useRef, useState } from 'react'
import './workflow.css'
import { CanvasBridge } from './CanvasBridge'
import type { CanvasBridgeTransport, HostInit, HostOpBatch, OperationRecord, Scene } from './Protocol/VersionedMessages'

type ViewRecord = OperationRecord & { record: Record<string, unknown> }

type CanvasView = {
  revision: number
  records: ViewRecord[]
}

function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback
}

function number(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

function recordTitle(record: ViewRecord): string {
  return text(record.record.title, text(record.record.name, record.recordType || '记录'))
}

function recordSummary(record: ViewRecord): string {
  return text(record.record.content, text(record.record.text, record.recordType || '暂无内容'))
}

function recordPosition(record: ViewRecord, index: number) {
  return {
    left: number(record.record.x, 72 + (index % 3) * 280),
    top: number(record.record.y, 72 + Math.floor(index / 3) * 190)
  }
}

function resetView(scene: Scene): CanvasView {
  return { revision: scene.revision, records: scene.snapshot.records as ViewRecord[] }
}

export function CanvasApp({ transport }: { transport: CanvasBridgeTransport }) {
  const bridge = useRef<CanvasBridge | null>(null)
  const [view, setView] = useState<CanvasView>({ revision: 0, records: [] })
  const [selected, setSelected] = useState<string | null>(null)
  const [capabilities, setCapabilities] = useState<HostInit['capabilities']>({
    serverClaims: [], canEditCanvas: false, canInvokeSkill: false, canCancelJob: false, canUndo: false, reason: '等待宿主初始化'
  })
  const [status, setStatus] = useState('等待宿主初始化')

  useEffect(() => {
    const instance = new CanvasBridge(transport, {
      onInit: (init) => {
        setCapabilities(init.capabilities)
        setView(resetView(init.scene))
        setSelected(null)
        setStatus(`已连接宿主 · 修订 ${init.scene.revision}`)
      },
      onSceneReset: (scene) => {
        setView(resetView(scene))
        setSelected(null)
        setStatus(`场景已重置 · 修订 ${scene.revision}`)
      },
      onHostOps: (batch: HostOpBatch) => {
        setView((current) => ({ revision: Math.max(current.revision, batch.revision), records: mergeRecords(current.records, batch.ops) }))
        setStatus(`已收到宿主更新 · 修订 ${batch.revision}`)
      }
    })
    bridge.current = instance
    instance.start()
    return () => instance.dispose()
  }, [transport])

  const selectedRecord = useMemo(() => view.records.find((record) => record.recordId === selected) ?? null, [selected, view.records])

  return (
    <div className="workflow-shell">
      <header className="workflow-header">
        <div className="brand-lockup"><strong>DreamForge</strong><span>节点工作流</span></div>
        <div className="header-status"><span>{status}</span><span>修订 {view.revision}</span></div>
      </header>
      <aside className="node-library">
        <strong>当前场景</strong>
        <div className="library-stat"><b>{view.records.length}</b><span>条宿主记录</span></div>
        <div className="library-note">此画布只展示宿主提供的真实场景数据。节点类型、标题和内容来自协议记录。</div>
        <div className="capability-list">
          <span className={capabilities.canEditCanvas ? 'available' : ''}>画布编辑</span>
          <span className={capabilities.canInvokeSkill ? 'available' : ''}>技能调用</span>
          <span className={capabilities.canCancelJob ? 'available' : ''}>任务取消</span>
          <span className={capabilities.canUndo ? 'available' : ''}>本地撤销</span>
        </div>
      </aside>
      <main className="workflow-canvas">
        <div className="canvas-grid" />
        {view.records.map((record, index) => {
          const position = recordPosition(record, index)
          return <article key={record.recordId} className={`workflow-node ${selected === record.recordId ? 'selected' : ''}`} style={position} onClick={() => setSelected(record.recordId)}>
            <div className="node-top"><span>{record.recordType || '记录'}</span><span className="node-id">{record.recordId.slice(0, 8)}</span></div>
            <h3>{recordTitle(record)}</h3>
            <p>{recordSummary(record)}</p>
            <div className="node-port input" /><div className="node-port output" />
          </article>
        })}
        {view.records.length === 0 && <div className="empty-canvas"><strong>当前没有可展示的节点</strong><span>等待宿主发送场景记录。</span></div>}
      </main>
      <aside className="inspector">
        <strong>记录详情</strong>
        {selectedRecord ? <>
          <div className="detail-row"><span>类型</span><b>{selectedRecord.recordType || '记录'}</b></div>
          <div className="detail-row"><span>记录 ID</span><code>{selectedRecord.recordId}</code></div>
          <label>标题<input value={recordTitle(selectedRecord)} readOnly /></label>
          <label>内容<textarea value={recordSummary(selectedRecord)} readOnly /></label>
          <div className="inspector-foot">当前宿主未声明画布编辑协议，详情为只读。</div>
        </> : <div className="inspector-empty">选择一个宿主记录查看详情。</div>}
      </aside>
    </div>
  )
}

function mergeRecords(current: ViewRecord[], incoming: OperationRecord[]): ViewRecord[] {
  const records = new Map(current.map((record) => [record.recordId, record]))
  incoming.forEach((record) => records.set(record.recordId, record as ViewRecord))
  return [...records.values()]
}
