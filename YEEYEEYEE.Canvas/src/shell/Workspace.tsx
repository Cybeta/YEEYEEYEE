import { useMemo, useRef, useState } from 'react'
import type { Asset } from '../assets'
import { ALL_CHAPTERS_ID } from '../ChapterView'
import { layerOf } from '../Protocol/VersionedMessages'
import { CanvasView, clampZoom, type CanvasApi, type Pan } from './CanvasView'
import { ghostMoves, type LayoutMove, type LayoutPlan, type LayoutScope } from './layoutPlan'
import { describeLease, treeLease, type Lease } from './locks'
import {
  chapterGroups, isEditableRecord, PLANNING_GROUP_ID, recordContent, recordTitle, scriptEntries,
  stageSummaries, UNFILED_GROUP_ID, type ShellEdge, type ViewRecord
} from './records'
import { ScriptView, TimelineView } from './views'
import type { WorkbenchView } from './WorkbenchShell'

/**
 * 中央工作流画布：标签条 + 工具栏 + 制作阶段芯片 + 三个视图。
 *
 * 桌面端这里是一个 Grid(RowDefinitions="Auto,Auto,Auto,*")，四个区的顺序与边距都一样。
 * 工具栏里还有几个动作在服务端没有对应接口（加资源、连接、删除），
 * 这里做成**可见但不可点并给出原因**——占位成可点、点了没反应，比灰着更让人困惑。
 * （新建与删除节点已经有了，但入口在工作树与检查器上，不是这几个按钮。）
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
  /** 角色层面能不能改（与 readOnly 合起来决定按钮给不给点）。 */
  canEdit: boolean
  assets: Asset[]
  assetsReady: boolean
  /** 当前有效的编辑锁：卡片徽标、左上角提示、以及整理按钮的可用性都看它。 */
  leases?: Lease[]
  myUserId?: string
  /** 通道自己的问题（锁读不到、推送断开），画在画布左上角那枚提示里。 */
  channelNotice?: string
  layoutBusy: boolean
  layoutPlan: LayoutPlan | null
  layoutScope: LayoutScope
  onLayoutPlan: (scope: LayoutScope) => void
  onLayoutApply: (overrideManual: boolean) => void
  onLayoutCancel: () => void
  /** 画布上的连线（服务端投影的）。画布会自己再按「两端都画得出来」筛一遍。 */
  edges?: ShellEdge[]
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

// 模块级空默认值：写在组件里的 `?? []` 每次都是新数组，会让 useMemo 白白重算。
const EMPTY_LEASES: Lease[] = []
const EMPTY_GHOSTS: LayoutMove[] = []
const EMPTY_EDGES: ShellEdge[] = []

export function Workspace(props: WorkspaceProps) {
  const { view, records, selectedId, onSelect, search, canvasTitle, activeChapter, onChapter, readOnly, canEdit, assets, assetsReady } = props
  const leases = props.leases ?? EMPTY_LEASES
  const edges = props.edges ?? EMPTY_EDGES
  const [stage, setStage] = useState('all')
  const [showReferencePreviews, setShowReferencePreviews] = useState(true)
  const [zoom, setZoom] = useState(0.86)
  const [pan, setPan] = useState<Pan>({ x: 24, y: 24 })
  // 「自动布局覆盖」这个确认不跨次保留：换一次预览就要重新勾，免得手滑带着上一次的同意。
  const [overrideManual, setOverrideManual] = useState(false)
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
  const ghosts = useMemo(() => (props.layoutPlan ? ghostMoves(props.layoutPlan) : EMPTY_GHOSTS), [props.layoutPlan])

  // 章节范围只在真的选中了某一章时才有意义：企划与未归档不是章节，不能拿去做范围。
  const chapterScope = activeChapter !== ALL_CHAPTERS_ID && activeChapter !== PLANNING_GROUP_ID && activeChapter !== UNFILED_GROUP_ID
    ? { id: activeChapter, label: groups.find((group) => group.id === activeChapter)?.label ?? '这一章' }
    : null

  const treeHolder = treeLease(leases.filter((lease) => lease.userId !== props.myUserId))
  const arrangeBlocked = !canEdit ? '你没有编辑权限'
    : readOnly ? '服务端把这张画布标成了只读'
      : treeHolder ? `${describeLease(treeHolder)} 正在整理整棵树，请稍后再试`
        : ''
  const arrangeDisabled = props.layoutBusy || !!props.layoutPlan || arrangeBlocked.length > 0

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
          <button type="button" className="df-mini-button" disabled title="新建节点的入口在工作树顶上（这里不再重复开一个）">＋ 节点</button>
          <button type="button" className="df-mini-button" disabled title="新增引用尚未接入服务端">＋ 资源</button>
          <button type="button" className="df-mini-button" disabled title="连线画得出来了，但拖一根新的还没有写路径">连接</button>
          <span className="df-divider-v" style={{ height: 18, margin: '0 4px' }} />
          <button type="button" className="df-mini-button" disabled title="删除节点的入口在检查器里（这里不再重复开一个）">删除</button>
          <span className="df-divider-v" style={{ height: 18, margin: '0 4px' }} />
          {/* 桌面端界面里**没有**手动整理的入口——那份泳道引擎只有 Agent 与测试在用，
              所以这个按钮是新增能力，不是复刻。服务端接口已经就位，且用的是同一份引擎。 */}
          <button
            type="button"
            className="df-mini-button"
            disabled={arrangeDisabled}
            title={arrangeBlocked || '按章节泳道重排节点位置：先给预览，确认后才写入'}
            onClick={() => { setOverrideManual(false); props.onLayoutPlan(props.layoutScope) }}
          >整理布局</button>
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

      {/* position: relative 是给底下那条整理预览条定位用的（它浮在画布上，不另占一行） */}
      <div style={{ position: 'relative', margin: '10px 12px 12px', minHeight: 0, height: '100%' }}>
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
            leases={leases}
            myUserId={props.myUserId}
            channelNotice={props.channelNotice}
            ghosts={ghosts}
            edges={edges}
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

        {props.layoutPlan && (
          <div className={`df-layout-bar${props.layoutPlan.blocking ? ' is-blocked' : ''}`}>
            {props.layoutPlan.blocking ? (
              <>
                <span className="df-layout-note">
                  布局被阻断，画布不会改动：
                  {props.layoutPlan.conflicts.filter((conflict) => conflict.blocking).map((conflict) => ` ${conflict.message}`).join('') || ' 存在阻断冲突'}
                </span>
                <button type="button" className="df-mini-button" onClick={props.onLayoutCancel}>取消</button>
              </>
            ) : (
              <>
                <span className="df-layout-scope">
                  范围
                  <button
                    type="button"
                    className={`df-chip${props.layoutScope === 'all' ? ' is-active' : ''}`}
                    disabled={props.layoutBusy}
                    onClick={() => props.onLayoutPlan('all')}
                  >整画布</button>
                  {chapterScope && (
                    <button
                      type="button"
                      className={`df-chip${props.layoutScope === chapterScope.id ? ' is-active' : ''}`}
                      disabled={props.layoutBusy}
                      onClick={() => props.onLayoutPlan(chapterScope.id)}
                    >只整理「{chapterScope.label}」</button>
                  )}
                </span>
                <span className="df-layout-note" title={props.layoutPlan.summary}>
                  {props.layoutPlan.changed
                    ? `${props.layoutPlan.moves.length} 个节点会移动（虚线卡是整理后的位置）`
                    : '位置已经符合泳道布局，无需改动'}
                </span>
                {props.layoutPlan.requiresConfirmation && (
                  <label className="df-checkbox">
                    <input
                      type="checkbox"
                      checked={overrideManual}
                      onChange={(event) => setOverrideManual(event.target.checked)}
                    />
                    连手动摆放的 {props.layoutPlan.protectedRecordIds.length} 个一起动
                  </label>
                )}
                <button type="button" className="df-mini-button" disabled={props.layoutBusy} onClick={props.onLayoutCancel}>取消</button>
                <button
                  type="button"
                  className="df-primary-button"
                  disabled={props.layoutBusy || !props.layoutPlan.changed}
                  onClick={() => props.onLayoutApply(overrideManual)}
                >{props.layoutBusy ? '处理中…' : '应用整理'}</button>
              </>
            )}
          </div>
        )}
      </div>
    </div>
  )
}
