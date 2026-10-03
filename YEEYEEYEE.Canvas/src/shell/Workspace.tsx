import { useMemo, useRef, useState } from 'react'
import type { Asset } from '../assets'
import { ALL_CHAPTERS_ID } from '../ChapterView'
import { layerOf } from '../Protocol/VersionedMessages'
import { CanvasView, clampZoom, type CanvasApi, type Pan } from './CanvasView'
import { chapterGroups, isEditableRecord, recordContent, recordTitle, scriptEntries, stageSummaries, type ViewRecord } from './records'
import { ScriptView, TimelineView } from './views'
import type { WorkbenchView } from './WorkbenchShell'

/**
 * 中央工作流画布：标签条 + 工具栏 + 制作阶段芯片 + 三个视图。
 *
 * 桌面端这里是一个 Grid(RowDefinitions="Auto,Auto,Auto,*")，四个区的顺序与边距都一样。
 * 工具栏里有几个按钮在服务端还没有对应接口（新建节点、加资源、连接、删除），
 * 这里做成**可见但不可点并给出原因**——占位成可点、点了没反应，比灰着更让人困惑。
 */

export type WorkspaceProps = {
  view: WorkbenchView
  records: ViewRecord[]
  selectedId: string
  /** 传 null 表示取消选中（在画布上点空白处）。 */
  onSelect: (recordId: string | null) => void
  search: string
  canvasTitle: string
  activeChapter: string
  onChapter: (chapterId: string) => void
  readOnly: boolean
  assets: Asset[]
  assetsReady: boolean
}

const STAGE_FILTERS: Array<{ key: string; label: string; match: (layer: number) => boolean }> = [
  { key: 'planning', label: '企划', match: (layer) => layer === 1 || layer === 2 },
  { key: 'chapter', label: '章节', match: (layer) => layer === 3 },
  { key: 'storyboard', label: '分镜', match: (layer) => layer === 4 },
  { key: 'product', label: '成品', match: (layer) => layer === 5 }
]

/** 搜索是**纯客户端**筛选：节点标题、内容、稳定 ID 三处命中任一即保留。 */
export function matchesSearch(record: ViewRecord, search: string): boolean {
  const needle = search.trim().toLowerCase()
  if (needle.length === 0) return true
  return recordTitle(record).toLowerCase().includes(needle)
    || recordContent(record).toLowerCase().includes(needle)
    || record.recordId.toLowerCase().includes(needle)
}

export function Workspace(props: WorkspaceProps) {
  const { view, records, selectedId, onSelect, search, canvasTitle, activeChapter, onChapter, readOnly, assets, assetsReady } = props
  const [stage, setStage] = useState('all')
  const [showReferencePreviews, setShowReferencePreviews] = useState(true)
  const [zoom, setZoom] = useState(0.86)
  const [pan, setPan] = useState<Pan>({ x: 24, y: 24 })
  const canvasApi = useRef<CanvasApi | null>(null)

  const summaries = useMemo(() => stageSummaries(records), [records])

  const visible = useMemo(() => {
    const stageMatcher = STAGE_FILTERS.find((item) => item.key === stage)?.match
    return records.filter((record) =>
      isEditableRecord(record)
      && (!stageMatcher || stageMatcher(layerOf(record.recordType)))
      && matchesSearch(record, search))
  }, [records, stage, search])

  const groups = useMemo(() => chapterGroups(visible), [visible])
  const entries = useMemo(() => scriptEntries(visible), [visible])

  return (
    <div className="df-center-grid">
      <div style={{ margin: '12px 12px 0' }}>
        <div className="df-card" style={{ background: 'var(--df-surface-1)', borderRadius: 'var(--df-radius-box)', padding: '3px 4px' }}>
          <div className="df-tabs df-scroll">
            <span className="df-tab-item is-active" title={canvasTitle}>{canvasTitle}</span>
            <button type="button" className="df-tab-item" disabled title="新建画布在网页端尚未接入">＋</button>
          </div>
        </div>
      </div>

      <div className="df-row-between" style={{ margin: '10px 12px 0' }}>
        <div className="df-cluster df-wrap">
          <button type="button" className="df-mini-button is-active">选择</button>
          <button type="button" className="df-mini-button" disabled title="网页端只开放「改标题与内容」这一条写路径，新建节点尚未接入">＋ 节点</button>
          <button type="button" className="df-mini-button" disabled title="新增引用尚未接入服务端">＋ 资源</button>
          <button type="button" className="df-mini-button" disabled title="连线未投影到网页端，也无法新建">连接</button>
          <span className="df-divider-v" style={{ height: 18, margin: '0 4px' }} />
          <button type="button" className="df-mini-button" disabled title="删除节点尚未接入服务端">删除</button>
          <span className="df-divider-v" style={{ height: 18, margin: '0 4px' }} />
          <label className="df-checkbox" style={{ margin: '0 4px' }}>
            <input
              type="checkbox"
              checked={showReferencePreviews}
              onChange={(event) => setShowReferencePreviews(event.target.checked)}
            />
            预览引用节点
          </label>
        </div>

        <div className="df-cluster">
          <button type="button" className="df-mini-button" onClick={() => canvasApi.current?.zoomBy(0.9)} disabled={view !== 'canvas'}>−</button>
          <span className="df-muted" style={{ fontSize: 11, margin: '0 7px' }}>{Math.round(zoom * 100)}%</span>
          <button type="button" className="df-mini-button" onClick={() => canvasApi.current?.zoomBy(1.1)} disabled={view !== 'canvas'}>＋</button>
          <button type="button" className="df-mini-button" onClick={() => canvasApi.current?.fit()} disabled={view !== 'canvas'}>适应</button>
        </div>
      </div>

      <div className="df-row df-scroll" style={{ margin: '10px 12px 0', gap: 6, overflowX: 'auto' }}>
        <button
          type="button"
          className={`df-chip${stage === 'all' ? ' is-active' : ''}`}
          onClick={() => setStage('all')}
        >全部 {records.filter(isEditableRecord).length}</button>
        {summaries.map((summary) => (
          <button
            key={summary.key}
            type="button"
            className={`df-chip${stage === summary.key ? ' is-active' : ''}`}
            onClick={() => setStage(summary.key)}
          >
            <span className="df-node-dot" style={{ background: summary.hex, display: 'inline-block', marginRight: 6 }} aria-hidden="true" />
            {summary.label} {summary.count}
          </button>
        ))}
        {readOnly && <span className="df-chip-static" title="服务端判定这张画布只能读">只读画布</span>}
      </div>

      <div style={{ margin: '10px 12px 12px', minHeight: 0, height: '100%' }}>
        {view === 'canvas' && (
          <CanvasView
            records={visible}
            selectedId={selectedId}
            activeChapter={activeChapter || ALL_CHAPTERS_ID}
            assets={assets}
            assetsReady={assetsReady}
            showReferencePreviews={showReferencePreviews}
            zoom={zoom}
            pan={pan}
            onZoom={(next) => setZoom(clampZoom(next))}
            onPan={setPan}
            onSelect={onSelect}
            apiRef={canvasApi}
          />
        )}
        {view === 'timeline' && (
          <div className="df-viewport-shell">
            <TimelineView
              groups={groups}
              activeChapter={activeChapter || ALL_CHAPTERS_ID}
              selectedId={selectedId}
              onChapter={onChapter}
              onSelect={onSelect}
            />
          </div>
        )}
        {view === 'script' && (
          <div className="df-viewport-shell">
            <ScriptView entries={entries} selectedId={selectedId} onSelect={onSelect} />
          </div>
        )}
      </div>
    </div>
  )
}
