import { useEffect, useMemo, useRef, useState } from 'react'
import './workflow.css'
import { CanvasBridge } from './CanvasBridge'
import type { CanvasBridgeTransport, HostInit, HostOpBatch, JobUpdate, OperationRecord, Scene } from './Protocol/VersionedMessages'
import { layerOf, isLayerPill, type NodeReferenceThumb, type NodeReferenceVersion } from './Protocol/VersionedMessages'
import { ALL_CHAPTERS_ID, chapterEntries, chapterIdOf, chapterLabelOf, filterByChapter, sortWithinChapters, type ChapterEntry } from './ChapterView'
import { hasBlockedReference, isLockedVersionMissing, referenceVersionLabel } from './ReferenceView'
import { describeJobAttempt, describeJobStatus, isRetryableJobState } from './JobView'

type ViewRecord = OperationRecord & { record: Record<string, unknown> }

type CanvasView = {
  revision: number
  records: ViewRecord[]
}

type DisplayMode = 'chapter' | 'overview'

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

function recordReferences(record: ViewRecord): NodeReferenceThumb[] {
  const raw = record.record.references
  if (!Array.isArray(raw)) return []
  return raw.filter((item): item is NodeReferenceThumb =>
    typeof item === 'object' && item !== null && typeof (item as NodeReferenceThumb).name === 'string'
  )
}

const LAYER_LABELS: Record<number, string> = {
  1: 'L1 剧情', 2: 'L2 企划', 3: 'L3 章节', 4: 'L4 分镜头', 5: 'L5 成品', 0: '其他'
}

const LAYER_Y: Record<number, number> = { 1: 48, 2: 128, 3: 208, 4: 300, 5: 470, 0: 560 }
const NODE_W = 230
const NODE_GAP = 40
const CANVAS_W = 1800
const CENTER_X = CANVAS_W / 2 - NODE_W / 2

type NodePosition = { left: number; top: number }
type NodeEdge = { id: string; from: NodePosition; to: NodePosition; fromHeight: number }

function estimatedNodeHeight(record: ViewRecord, collapsed: Set<string>): number {
  const refs = recordReferences(record)
  if (refs.length === 0 || collapsed.has(record.recordId)) return 130
  return 158 + Math.ceil(refs.length / 3) * 76
}

function parentIdOf(record: ViewRecord): string {
  return text(record.parentId, text(record.record.parentId, text((record as unknown as { parentId?: unknown }).parentId)))
}

function computeEdges(records: ViewRecord[], positions: Map<string, NodePosition>, collapsed: Set<string>): NodeEdge[] {
  const byId = new Map(records.map((record) => [record.recordId, record]))
  return records.flatMap((record) => {
    const parentId = parentIdOf(record)
    const parent = byId.get(parentId)
    const from = positions.get(parentId)
    const to = positions.get(record.recordId)
    if (!parent || !from || !to) return []
    return [{ id: `${parentId}->${record.recordId}`, from, to, fromHeight: estimatedNodeHeight(parent, collapsed) }]
  })
}

function computeLayout(records: ViewRecord[], collapsed: Set<string>): Map<string, NodePosition> {
  const positions = new Map<string, { left: number; top: number }>()
  const layers = new Map<number, ViewRecord[]>()

  for (const r of records) {
    const layer = layerOf(r.recordType)
    const arr = layers.get(layer) ?? []
    arr.push(r)
    layers.set(layer, arr)
  }

  let previousBottom = 0
  for (const [layer, items] of [...layers.entries()].sort((a, b) => (LAYER_Y[a[0]] ?? 560) - (LAYER_Y[b[0]] ?? 560))) {
    const baseY = LAYER_Y[layer] ?? 560
    const y = Math.max(baseY, previousBottom + 34)
    if (isLayerPill(layer) && items.length <= 2) {
      items.forEach((r, i) => {
        const offset = items.length === 1 ? 0 : (i - 0.5) * (NODE_W + NODE_GAP * 2)
        positions.set(r.recordId, { left: CENTER_X + offset, top: y })
      })
    } else {
      const totalWidth = items.length * NODE_W + (items.length - 1) * NODE_GAP
      const startX = Math.max(60, (CANVAS_W - totalWidth) / 2)
      items.forEach((r, i) => {
        positions.set(r.recordId, { left: startX + i * (NODE_W + NODE_GAP), top: y })
      })
    }
    previousBottom = y + Math.max(...items.map((item) => estimatedNodeHeight(item, collapsed)))
  }

  return positions
}

function resetView(scene: Scene): CanvasView {
  return {
    revision: scene.revision,
    records: mergeRecords([], scene.snapshot.records).filter((record) => !record.deleted)
  }
}

const ENTITY_LABELS: Record<string, string> = { Character: '角色', Scene: '场景', Prop: '道具' }
const CANVAS_STATE_KEY = 'yeeeyee.canvas.view-state'

// 视图状态只用于界面偏好（C-4）：存的是稳定章节 ID，不是章节名文本。
// 旧版本存过章节名（chapterFilter），名称不能当作身份，读到时一律丢弃并回到「全部章节」。
type CanvasState = { displayMode: DisplayMode; chapterFilterId: string; collapsedIds: string[] }

function readCanvasState(): CanvasState {
  const fallback: CanvasState = { displayMode: 'chapter', chapterFilterId: ALL_CHAPTERS_ID, collapsedIds: [] }
  try {
    const raw = localStorage.getItem(CANVAS_STATE_KEY)
    if (!raw) return fallback
    const parsed = JSON.parse(raw) as Partial<CanvasState>
    return {
      displayMode: parsed.displayMode === 'overview' ? 'overview' : 'chapter',
      chapterFilterId: typeof parsed.chapterFilterId === 'string' ? parsed.chapterFilterId : ALL_CHAPTERS_ID,
      collapsedIds: Array.isArray(parsed.collapsedIds) ? parsed.collapsedIds.filter((id): id is string => typeof id === 'string') : []
    }
  } catch {
    return fallback
  }
}

export function CanvasApp({ transport }: { transport: CanvasBridgeTransport }) {
  const bridge = useRef<CanvasBridge | null>(null)
  const [view, setView] = useState<CanvasView>({ revision: 0, records: [] })
  const [selected, setSelected] = useState<string | null>(null)
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set(readCanvasState().collapsedIds))
  const [displayMode, setDisplayMode] = useState<DisplayMode>(() => readCanvasState().displayMode)
  const [chapterFilterId, setChapterFilterId] = useState(() => readCanvasState().chapterFilterId)
  const [resourceSelection, setResourceSelection] = useState<NodeReferenceThumb | null>(null)
  const [selectedVersionId, setSelectedVersionId] = useState<string>('latest')
  const [capabilities, setCapabilities] = useState<HostInit['capabilities']>({
    serverClaims: [], canEditCanvas: false, canInvokeSkill: false, canCancelJob: false, canUndo: false, reason: '等待宿主初始化'
  })
  const [status, setStatus] = useState('等待宿主初始化')
  // 最近一次任务状态（目标 5）：与桌面显示同一条尝试链（第几次尝试、重试自哪个任务）。
  const [job, setJob] = useState<JobUpdate | null>(null)

  useEffect(() => {
    const instance = new CanvasBridge(transport, {
      onInit: (init) => {
        setCapabilities(init.capabilities)
        setView(resetView(init.scene))
        setSelected(null)
        setCollapsed(new Set())
        setStatus(`已连接宿主 · 修订 ${init.scene.revision}`)
      },
      onSceneReset: (scene) => {
        setView(resetView(scene))
        setSelected(null)
        setCollapsed(new Set())
        setStatus(`场景已重置 · 修订 ${scene.revision}`)
      },
      onHostOps: (batch: HostOpBatch) => {
        setView((current) => {
          if (batch.revision !== current.revision + 1) {
            setStatus(`远端修订不连续（当前 ${current.revision}，收到 ${batch.revision}），等待场景重置`)
            return current
          }
          setStatus(`已收到宿主更新 · 修订 ${batch.revision}`)
          return { revision: batch.revision, records: mergeRecords(current.records, batch.ops) }
        })
      },
      onResourceReplaceResult: (result) => {
        setStatus(result.ok
          ? `${result.message}${result.revision ? ` · 修订 ${result.revision}` : ''}`
          : `资源版本替换失败：${result.message}`)
      },
      onJobUpdate: (update) => {
        setJob(update)
        setStatus(describeJobStatus(update))
      }
    })
    bridge.current = instance
    instance.start()
    return () => instance.dispose()
  }, [transport])

  // 章节选项来自稳定 ID + 显式顺序；同名不同 ID 的章节是两条，不会合并（C-4）。
  const chapters = useMemo<ChapterEntry[]>(() => chapterEntries(view.records), [view.records])
  const displayedRecords = useMemo(() => {
    if (displayMode === 'overview') return sortWithinChapters(view.records)
    return sortWithinChapters(filterByChapter(view.records, chapterFilterId))
  }, [displayMode, chapterFilterId, view.records])
  const positions = useMemo(() => computeLayout(displayedRecords, collapsed), [displayedRecords, collapsed])
  const edges = useMemo(() => computeEdges(displayedRecords, positions, collapsed), [displayedRecords, positions, collapsed])
  const selectedRecord = useMemo(() => view.records.find((record) => record.recordId === selected) ?? null, [selected, view.records])
  const selectedResourceOwner = useMemo(
    () => displayedRecords.find((record) => recordReferences(record).some((ref) => ref.entityId === resourceSelection?.entityId)) ?? null,
    [displayedRecords, resourceSelection]
  )

  useEffect(() => {
    if (selected && !displayedRecords.some((record) => record.recordId === selected)) {
      setSelected(null)
      setResourceSelection(null)
    }
  }, [displayedRecords, selected])

  useEffect(() => {
    if (chapterFilterId !== ALL_CHAPTERS_ID && !chapters.some((chapter) => chapter.id === chapterFilterId)) {
      setChapterFilterId(ALL_CHAPTERS_ID)
    }
  }, [chapterFilterId, chapters])

  useEffect(() => {
    try {
      localStorage.setItem(CANVAS_STATE_KEY, JSON.stringify({
        displayMode,
        chapterFilterId,
        collapsedIds: [...collapsed]
      } satisfies CanvasState))
    } catch {
      // 宿主 WebView 可能禁用本地存储，不影响画布使用。
    }
  }, [collapsed, displayMode, chapterFilterId])

  const layers = useMemo(() => {
    const map = new Map<number, ViewRecord[]>()
    for (const r of displayedRecords) {
      const layer = layerOf(r.recordType)
      const arr = map.get(layer) ?? []
      arr.push(r)
      map.set(layer, arr)
    }
    return [...map.entries()].sort((a, b) => a[0] - b[0])
  }, [displayedRecords])

  function toggleCollapse(id: string) {
    setCollapsed((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  return (
    <div className="workflow-shell">
      <header className="workflow-header">
        <div className="brand-lockup"><strong>YEEYEEYEE</strong><span>节点工作流</span></div>
        <div className="header-status"><span>{status}</span><span>修订 {view.revision}</span></div>
      </header>
      <aside className="node-library">
        <strong>当前场景</strong>
        <div className="library-stat"><b>{view.records.length}</b><span>条宿主记录</span></div>
        <div className="library-note">画布按层级排列节点，分镜头节点内嵌引用元素缩略图。</div>
        <div className="layer-legend">
          {layers.map(([layer, items]) => (
            <div key={layer} className="layer-legend-row">
              <span className="layer-legend-label">{LAYER_LABELS[layer] ?? `L${layer}`}</span>
              <span className="layer-legend-count">{items.length}</span>
            </div>
          ))}
        </div>
        <div className="capability-list">
          <span className={capabilities.canEditCanvas ? 'available' : ''}>画布编辑</span>
          <span className={capabilities.canInvokeSkill ? 'available' : ''}>技能调用</span>
          <span className={capabilities.canCancelJob ? 'available' : ''}>任务取消</span>
          <span className={capabilities.canUndo ? 'available' : ''}>本地撤销</span>
        </div>
        {job && (
          <div className={`job-status ${isRetryableJobState(job.state) ? 'retryable' : ''}`} title={describeJobAttempt(job)}>
            <span className="job-title">任务 {job.jobId.slice(0, 8)}</span>
            <span className="job-detail">{describeJobStatus(job)}</span>
          </div>
        )}
      </aside>
      <main className="workflow-canvas">
        <div className="canvas-toolbar">
          <div className="view-switch" role="group" aria-label="画布视图">
            <button className={displayMode === 'chapter' ? 'active' : ''} onClick={() => setDisplayMode('chapter')}>章节视图</button>
            <button className={displayMode === 'overview' ? 'active' : ''} onClick={() => setDisplayMode('overview')}>概览视图</button>
          </div>
          <label className="chapter-picker">当前章节
            <select value={chapterFilterId} onChange={(event) => setChapterFilterId(event.target.value)}>
              <option value={ALL_CHAPTERS_ID}>全部章节</option>
              {chapters.map((chapter) => (
                <option key={chapter.id} value={chapter.id}>
                  {chapter.order === Number.MAX_SAFE_INTEGER ? chapter.label : `#${chapter.order} ${chapter.label}`}
                </option>
              ))}
            </select>
          </label>
          <span className="toolbar-count">显示 {displayedRecords.length} / {view.records.length}</span>
          <span className="toolbar-count">章节 {chapters.length} 条（按稳定 ID 与显式顺序）</span>
        </div>
        <div className="canvas-grid" />
        {layers.map(([layer, items]) => (
          <div key={layer} className="layer-rail" style={{ top: (LAYER_Y[layer] ?? 560) - 20 }}>
            <span className="layer-rail-label">{LAYER_LABELS[layer] ?? '其他'}</span>
          </div>
        ))}
        <svg className="canvas-edges" width={CANVAS_W} height={1100} aria-hidden="true">
          {edges.map((edge) => {
            const x1 = edge.from.left + NODE_W / 2
            const y1 = edge.from.top + edge.fromHeight
            const x2 = edge.to.left + NODE_W / 2
            const y2 = edge.to.top + 10
            const midY = (y1 + y2) / 2
            return <path key={edge.id} className="canvas-edge" d={`M ${x1} ${y1} C ${x1} ${midY}, ${x2} ${midY}, ${x2} ${y2}`} />
          })}
        </svg>
        {displayedRecords.map((record) => {
          const pos = positions.get(record.recordId) ?? { left: 60, top: 560 }
          const layer = layerOf(record.recordType)
          const refs = recordReferences(record)
          const isCollapsed = collapsed.has(record.recordId)
          const pill = isLayerPill(layer) && items_in_layer(displayedRecords, layer) <= 2
          return (
            <article
              key={record.recordId}
              className={`workflow-node ${selected === record.recordId ? 'selected' : ''} ${pill ? 'pill-node' : ''} ${refs.length > 0 ? 'has-refs' : ''} ${hasBlockedReference(refs) ? 'ref-blocked' : ''}`}
              style={pos}
              onClick={() => setSelected(record.recordId)}
            >
              <div className="node-top">
                <span>{record.recordType || '记录'}</span>
                <span className="node-id">{record.recordId.slice(0, 8)}</span>
              </div>
              <div className="node-chapter">
                章节：{chapterLabelOf(record)}{chapterIdOf(record) ? ` · ${chapterIdOf(record).slice(0, 8)}` : ' · 无稳定 ID'}
              </div>
              <h3>{recordTitle(record)}</h3>
              <p>{recordSummary(record)}</p>
              {refs.length > 0 && (
                <div className="ref-strip">
                  <div className="ref-strip-header" onClick={(e) => { e.stopPropagation(); toggleCollapse(record.recordId) }}>
                    <span>引用 ({refs.length})</span>
                    {hasBlockedReference(refs) && <span className="ref-blocked-tag">锁定版本缺失</span>}
                    <span className="ref-toggle">{isCollapsed ? '▾ 展开' : '▴ 折叠'}</span>
                  </div>
                  {!isCollapsed && (
                    <div className="ref-cards">
                      {refs.map((ref, i) => (
                        <div key={i} className={`ref-card ${resourceSelection?.entityId === ref.entityId ? 'selected' : ''}`} onClick={(e) => { e.stopPropagation(); setSelected(record.recordId); setResourceSelection(ref); setSelectedVersionId(ref.variantVersionId ?? 'latest'); bridge.current?.send('canvas/selection.changed', { recordId: record.recordId, entityId: ref.entityId, entityKind: ref.kind }) }}>
                          <div className="ref-thumb">
                            {ref.thumbnailRef
                              ? <img src={ref.thumbnailRef} alt={ref.name} className="ref-thumb-img" />
                              : <div className="ref-thumb-placeholder">{ENTITY_LABELS[ref.kind]?.[0] ?? '?'}</div>}
                          </div>
                          <span className={`ref-kind-tag ${ref.kind.toLowerCase()}`}>{ENTITY_LABELS[ref.kind] ?? ref.kind}</span>
                          <span className="ref-name">{ref.name}</span>
                          <span className={`ref-variant ${isLockedVersionMissing(ref) ? 'blocked' : ''}`}>{referenceVersionLabel(ref)}</span>
                        </div>
                      ))}
                    </div>
                  )}
                  {isCollapsed && (
                    <div className="ref-collapsed-tags">
                      {refs.slice(0, 5).map((ref, i) => (
                        <span key={i} className={`ref-tag ${ref.kind.toLowerCase()}`}>{ref.name}</span>
                      ))}
                      {refs.length > 5 && <span className="ref-tag more">+{refs.length - 5}</span>}
                    </div>
                  )}
                </div>
              )}
              <div className="node-port input" /><div className="node-port output" />
            </article>
          )
        })}
        {displayedRecords.length === 0 && <div className="empty-canvas"><strong>当前视图没有可展示的节点</strong><span>请选择其他章节或切换到概览视图。</span></div>}
      </main>
      <aside className="inspector">
        <strong>记录详情</strong>
        {selectedRecord ? <>
          <div className="detail-row"><span>类型</span><b>{selectedRecord.recordType || '记录'}</b></div>
          <div className="detail-row"><span>记录 ID</span><code>{selectedRecord.recordId}</code></div>
          <div className="detail-row"><span>层级</span><b>{LAYER_LABELS[layerOf(selectedRecord.recordType)] ?? '其他'}</b></div>
          <div className="detail-row"><span>章节</span><b>{chapterLabelOf(selectedRecord)}</b></div>
          <div className="detail-row"><span>章节 ID</span><code>{chapterIdOf(selectedRecord) || '无稳定 ID（不参与章节绑定）'}</code></div>
          {chapterIdOf(selectedRecord) && (
            <button className="inspector-locate" onClick={() => setChapterFilterId(chapterIdOf(selectedRecord))}>定位到该章节</button>
          )}
          <label>标题<input value={recordTitle(selectedRecord)} readOnly /></label>
          <label>内容<textarea value={recordSummary(selectedRecord)} readOnly /></label>
          {recordReferences(selectedRecord).length > 0 && (
            <div className="inspector-refs">
              <strong>引用元素</strong>
              {recordReferences(selectedRecord).map((ref, i) => (
                <button key={i} className={`inspector-ref-item ${resourceSelection?.entityId === ref.entityId ? 'selected' : ''}`} onClick={() => { setResourceSelection(ref); setSelectedVersionId(ref.variantVersionId ?? 'latest'); bridge.current?.send('canvas/selection.changed', { recordId: selectedRecord.recordId, entityId: ref.entityId, entityKind: ref.kind }) }}>
                  <span className={`ref-kind-tag ${ref.kind.toLowerCase()}`}>{ENTITY_LABELS[ref.kind] ?? ref.kind}</span>
                  <span className="ref-name">{ref.name}</span>
                  {ref.variantLabel && <span className="ref-variant">{ref.variantLabel}</span>}
                </button>
              ))}
            </div>
          )}
          {resourceSelection && (
            <div className="resource-detail">
              <div className="resource-detail-heading"><strong>资源库引用</strong><span>{ENTITY_LABELS[resourceSelection.kind]}</span></div>
              <div className="resource-detail-name">{resourceSelection.name}</div>
              <div className="detail-row"><span>资源 ID</span><code>{resourceSelection.entityId}</code></div>
              <div className="detail-row">
                <span>引用版本</span>
                <b className={isLockedVersionMissing(resourceSelection) ? 'blocked-version' : ''}>{referenceVersionLabel(resourceSelection)}</b>
              </div>
              {isLockedVersionMissing(resourceSelection) && (
                <div className="version-blocked-note">锁定的版本已不存在：请改回「跟随最新」或重新锁定一个存在的版本，不要按当前内容继续使用。</div>
              )}
              {resourceSelection.versions && resourceSelection.versions.length > 0 && (
                <label className="version-picker-label">引用版本
                  <select
                    className="version-picker"
                    value={selectedVersionId}
                    disabled={!capabilities.canEditCanvas || !selectedRecord || !resourceSelection.variantId}
                    onChange={(event) => {
                      const nextId = event.target.value
                      setSelectedVersionId(nextId)
                      if (!selectedRecord || !resourceSelection.variantId) return
                      bridge.current?.send('canvas/resource.replace.request', {
                        recordId: selectedRecord.recordId,
                        entityId: resourceSelection.entityId,
                        variantId: resourceSelection.variantId,
                        variantVersionId: nextId === 'latest' ? null : nextId
                      })
                    }}
                  >
                    <option value="latest">跟随最新内容</option>
                    {resourceSelection.versions.map((version: NodeReferenceVersion) => (
                      <option key={version.id} value={version.id}>
                        {version.label}{version.note ? ` · ${version.note}` : ''}
                      </option>
                    ))}
                  </select>
                </label>
              )}
              {selectedResourceOwner && <div className="resource-owner">来自：{recordTitle(selectedResourceOwner)}</div>}
              <button className="resource-action" onClick={() => bridge.current?.send('canvas/selection.changed', { entityId: resourceSelection.entityId, entityKind: resourceSelection.kind, openResourceLibrary: true })}>定位资源库</button>
              <button
                className="resource-action"
                disabled={!capabilities.canEditCanvas || !selectedRecord || !resourceSelection.variantId || !resourceSelection.variantVersionId}
                onClick={() => {
                  if (!selectedRecord || !resourceSelection.variantId || !resourceSelection.variantVersionId) return
                  bridge.current?.send('canvas/resource.replace.request', {
                    recordId: selectedRecord.recordId,
                    entityId: resourceSelection.entityId,
                    variantId: resourceSelection.variantId,
                    variantVersionId: resourceSelection.variantVersionId
                  })
                }}
              >锁定当前版本</button>
              <button
                className="resource-action"
                disabled={!capabilities.canEditCanvas || !selectedRecord || !resourceSelection.variantId}
                onClick={() => {
                  if (!selectedRecord || !resourceSelection.variantId) return
                  bridge.current?.send('canvas/resource.replace.request', {
                    recordId: selectedRecord.recordId,
                    entityId: resourceSelection.entityId,
                    variantId: resourceSelection.variantId,
                    variantVersionId: null
                  })
                }}
              >跟随最新版本</button>
            </div>
          )}
          <div className="inspector-foot">{capabilities.canEditCanvas ? '宿主已声明画布编辑能力，可发起资源操作。' : '当前宿主未声明资源编辑协议，定位可用，替换按钮保持只读。'}</div>
        </> : <div className="inspector-empty">选择一个宿主记录查看详情。</div>}
      </aside>
    </div>
  )
}

function items_in_layer(records: ViewRecord[], layer: number): number {
  return records.filter((r) => layerOf(r.recordType) === layer).length
}

function mergeRecords(current: ViewRecord[], incoming: OperationRecord[]): ViewRecord[] {
  const records = new Map(current.map((record) => [record.recordId, record]))
  incoming.forEach((record) => {
    if (record.deleted) records.delete(record.recordId)
    else records.set(record.recordId, record as ViewRecord)
  })
  return [...records.values()]
}
