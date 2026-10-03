import { useCallback, useEffect, useMemo, useRef } from 'react'
import { recordReferences, resolveReference, type Asset } from '../assets'
import { ALL_CHAPTERS_ID } from '../ChapterView'
import {
  canvasBounds, chapterGroups, isEditableRecord, kindOf, NODE_KINDS, nodeX, nodeY,
  recordContent, recordStatus, recordTitle, type ViewRecord
} from './records'

/**
 * 无限画布视图：按节点在画布上的**真实坐标**摆放。
 *
 * 桌面端的 CanvasSurface 是 3103 行命令式代码（一行行 new Border/TextBlock 搭卡片、
 * 自己维护指针状态机、自己量卡片高度去对齐连线端点）。这里不照抄那套结构——
 * 其中相当一部分是在为 Avalonia 的约束付代价（卡片 MinHeight、高度要等布局跑完才知道、
 * 世界画布要固定 4200×3000 才不被压缩）。DOM 里这些约束不存在：
 * 卡片用普通流式布局，缩放平移交给一条 transform。
 *
 * 两点如实说明，不假装有：
 *   - 连线没有投影到网页端（NodeProjection 只投影节点），所以这里画不出连线；
 *   - 网页端目前只有「改标题与内容」这一条写路径，增删节点与连线都没有接口。
 */

export type Pan = { x: number; y: number }

/**
 * 画布对外的命令接口。工具栏上的「− / ＋ / 适应」要用它——
 * 缩放锚点得按视口的真实位置算，所以这些动作只能由画布自己执行，光有 zoom 状态不够。
 */
export type CanvasApi = {
  fit: () => void
  zoomBy: (factor: number) => void
}

export type CanvasViewProps = {
  records: ViewRecord[]
  selectedId: string
  activeChapter: string
  assets: Asset[]
  assetsReady: boolean
  showReferencePreviews: boolean
  zoom: number
  pan: Pan
  onZoom: (zoom: number) => void
  onPan: (pan: Pan) => void
  /** 传 null 表示取消选中（点空白处）。 */
  onSelect: (recordId: string | null) => void
  apiRef?: { current: CanvasApi | null }
}

const MIN_ZOOM = 0.25
const MAX_ZOOM = 2.2

export function clampZoom(value: number): number {
  return Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, value))
}

/**
 * 把浏览器给的滚动量换算成「格」。桌面端拿到的是 Avalonia 的 Delta.Y（一格 ≈ ±1），
 * 而浏览器按 deltaMode 给三种单位：像素（Chrome/Edge，一格约 100）、行（Firefox，一格 3 行）、页。
 * 三种都必须归到同一个「一格」，否则同一台机器换个浏览器滚轮手感就变了。
 */
export function wheelNotches(deltaY: number, deltaMode: number): number {
  if (deltaMode === 1) return deltaY / 3
  if (deltaMode === 2) return deltaY
  return deltaY / 100
}

/** 滚轮缩放倍率：与桌面端同一条曲线 exp(格数 × 0.12)，一格约 1.1275 倍。DOM 向上滚是负值，所以取反。 */
export function wheelZoomFactor(deltaY: number, deltaMode: number): number {
  return Math.exp(-wheelNotches(deltaY, deltaMode) * 0.12)
}

/** 缩放后让光标下的那个世界坐标点停在原地——否则放大时画面会往左上角跑。 */
export function zoomAround(pan: Pan, zoom: number, next: number, cursor: { x: number; y: number }): Pan {
  const worldX = (cursor.x - pan.x) / zoom
  const worldY = (cursor.y - pan.y) / zoom
  return { x: cursor.x - worldX * next, y: cursor.y - worldY * next }
}

export function CanvasView(props: CanvasViewProps) {
  const { records, selectedId, activeChapter, assets, assetsReady, showReferencePreviews, zoom, pan, onZoom, onPan, onSelect, apiRef } = props
  const viewportRef = useRef<HTMLDivElement>(null)
  const panning = useRef<{ pointerId: number; startX: number; startY: number; originX: number; originY: number } | null>(null)

  const bounds = useMemo(() => canvasBounds(records), [records])
  const nodes = useMemo(() => records.filter(isEditableRecord), [records])

  /**
   * 「只看这一章」压暗的是**不在这一组里**的节点。
   * 用分组结果反推成员，而不是拿 chapterId 直接比：企划组与未归档组根本没有一个能比的 chapterId，
   * 直接比会把这两组永远压暗（点了没反应）。
   */
  const activeIds = useMemo(() => {
    if (activeChapter === ALL_CHAPTERS_ID) return null
    const group = chapterGroups(records).find((item) => item.id === activeChapter)
    return new Set(group ? group.records.map((item) => item.recordId) : [])
  }, [records, activeChapter])

  // 滚轮缩放要 preventDefault，阻止浏览器把整页缩放掉。React 的 onWheel 是被动监听，
  // 拿不到取消权，所以这里挂原生监听。
  useEffect(() => {
    const element = viewportRef.current
    if (!element) return
    const onWheel = (event: WheelEvent) => {
      event.preventDefault()
      const rect = element.getBoundingClientRect()
      const cursor = { x: event.clientX - rect.left, y: event.clientY - rect.top }
      const next = clampZoom(zoom * wheelZoomFactor(event.deltaY, event.deltaMode))
      // 与桌面端同样的容差判断：贴到边界时不再来回重算。
      if (Math.abs(next - zoom) < 0.0005) return
      onPan(zoomAround(pan, zoom, next, cursor))
      onZoom(next)
    }
    element.addEventListener('wheel', onWheel, { passive: false })
    return () => element.removeEventListener('wheel', onWheel)
  }, [zoom, pan, onZoom, onPan])

  const fit = useCallback(() => {
    const element = viewportRef.current
    if (!element) return
    const rect = element.getBoundingClientRect()
    if (!nodes.length) {
      // 桌面端 ApplyFit 在空画布上的取值。
      onZoom(1)
      onPan({ x: 40, y: 40 })
      return
    }
    let minX = Number.POSITIVE_INFINITY
    let minY = Number.POSITIVE_INFINITY
    let maxX = Number.NEGATIVE_INFINITY
    let maxY = Number.NEGATIVE_INFINITY
    for (const node of nodes) {
      minX = Math.min(minX, nodeX(node))
      minY = Math.min(minY, nodeY(node))
      maxX = Math.max(maxX, nodeX(node) + 232)
      maxY = Math.max(maxY, nodeY(node) + 104)
    }
    const pad = 48
    const width = maxX - minX + pad * 2
    const height = maxY - minY + pad * 2
    // 桌面端整画布适应把上限压在 1.0：「适应」只用来看清全部，不用来放大。
    const next = Math.max(MIN_ZOOM, Math.min(1, Math.min(rect.width / width, rect.height / height)))
    onZoom(next)
    onPan({ x: (rect.width - width * next) / 2 - (minX - pad) * next, y: (rect.height - height * next) / 2 - (minY - pad) * next })
  }, [nodes, onZoom, onPan])

  /**
   * 首次拿到节点后自动适应一次；之后只有用户点「适应」才动视角。
   * 每次记录变化都重适应会把用户刚拖好的位置拽走。
   *
   * 这里与桌面端**有意不一致**：桌面端打开画布时不自动适应（停在上次的 0.86 / (24,24)），
   * 因为它有一个明确的「打开画布」动作；网页端打开页面就等于打开画布，没有那个动作可挂，
   * 不主动给一次「看得见全部」的视角，节点放在远处时用户会先看到一片空白。
   */
  const fitted = useRef(false)
  useEffect(() => {
    if (fitted.current || nodes.length === 0) return
    fitted.current = true
    fit()
  }, [nodes, fit])

  useEffect(() => {
    const element = viewportRef.current
    if (!element) return
    const onPointerDown = (event: PointerEvent) => {
      if (event.button !== 0) return
      // 按在节点卡上不要开始平移：那是「选中/拖节点」的手势。原生监听在 viewport 这一层先于
      // React 的合成事件触发，所以 React 那边的 stopPropagation 拦不住它，只能在这里判。
      if ((event.target as HTMLElement | null)?.closest('.df-node')) return
      // 与桌面端一致：按空白处先取消选中，再进入平移。
      onSelect(null)
      panning.current = { pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, originX: pan.x, originY: pan.y }
      element.setPointerCapture(event.pointerId)
      element.classList.add('is-panning')
    }
    const onPointerMove = (event: PointerEvent) => {
      const state = panning.current
      if (!state || state.pointerId !== event.pointerId) return
      onPan({ x: state.originX + (event.clientX - state.startX), y: state.originY + (event.clientY - state.startY) })
    }
    const end = (event: PointerEvent) => {
      const state = panning.current
      if (!state || state.pointerId !== event.pointerId) return
      panning.current = null
      if (element.hasPointerCapture(event.pointerId)) element.releasePointerCapture(event.pointerId)
      element.classList.remove('is-panning')
    }
    element.addEventListener('pointerdown', onPointerDown)
    element.addEventListener('pointermove', onPointerMove)
    element.addEventListener('pointerup', end)
    element.addEventListener('pointercancel', end)
    return () => {
      element.removeEventListener('pointerdown', onPointerDown)
      element.removeEventListener('pointermove', onPointerMove)
      element.removeEventListener('pointerup', end)
      element.removeEventListener('pointercancel', end)
    }
  }, [pan.x, pan.y, onPan, onSelect])

  // 把命令挂给工具栏：缩放锚点取视口中心，与滚轮缩放走同一条换算（zoomAround）。
  useEffect(() => {
    if (!apiRef) return
    apiRef.current = {
      fit,
      zoomBy: (factor: number) => {
        const element = viewportRef.current
        if (!element) return
        const rect = element.getBoundingClientRect()
        const next = clampZoom(zoom * factor)
        if (next === zoom) return
        onPan(zoomAround(pan, zoom, next, { x: rect.width / 2, y: rect.height / 2 }))
        onZoom(next)
      }
    }
    return () => { apiRef.current = null }
  }, [apiRef, fit, zoom, pan, onZoom, onPan])

  return (
    <div className="df-viewport-shell">
      {/* 双击**不做**适应：桌面端的双击（落在节点上）是进引用画布，落在空白处没有动作。
          给它加一个桌面端没有的手势，等于让同一个动作在两端有两种含义。 */}
      <div className="df-viewport" ref={viewportRef}>
        <div className="df-world" style={{ width: bounds.width, height: bounds.height, transform: `translate(${pan.x}px, ${pan.y}px) scale(${zoom})` }}>
          <div className="df-world-grid" />
          {nodes.map((node) => (
            <NodeCard
              key={node.recordId}
              node={node}
              selected={node.recordId === selectedId}
              dimmed={activeIds !== null && !activeIds.has(node.recordId)}
              assets={assets}
              assetsReady={assetsReady}
              showReferences={showReferencePreviews}
              onSelect={onSelect}
            />
          ))}
        </div>
        <div className="df-scanband" aria-hidden="true" />
      </div>
      {/* 桌面端那句提示末尾还有「Delete 删除」；网页端没有删除接口，所以不写上去。 */}
      <div className="df-hint">拖拽平移 · 滚轮缩放 · 点击节点选中</div>
    </div>
  )
}

function NodeCard({ node, selected, dimmed, assets, assetsReady, showReferences, onSelect }: {
  node: ViewRecord
  selected: boolean
  dimmed: boolean
  assets: Asset[]
  assetsReady: boolean
  showReferences: boolean
  onSelect: (recordId: string) => void
}) {
  const kind = kindOf(node.recordType)
  const meta = NODE_KINDS[kind]
  const references = recordReferences(node)
  return (
    <button
      type="button"
      className={`df-node${selected ? ' is-selected' : ''}`}
      style={{ left: nodeX(node), top: nodeY(node), opacity: dimmed ? 0.35 : 1 }}
      onPointerDown={(event) => event.stopPropagation()}
      onClick={(event) => { event.stopPropagation(); onSelect(node.recordId) }}
      title={`${meta.label} · ${recordStatus(node)}`}
    >
      <span className="df-node-kind">
        <span className="df-node-dot" style={{ background: meta.hex }} aria-hidden="true" />
        <span>{meta.label}</span>
      </span>
      <span className="df-node-title">{recordTitle(node)}</span>
      <span className="df-node-body">{recordContent(node) || '暂无内容'}</span>
      {showReferences && references.length > 0 && (
        <span className="df-node-refs">
          {references.map((reference, index) => {
            const resolved = resolveReference(reference, assets, assetsReady)
            return (
              <span key={`${reference.entityId}-${index}`} className={`df-ref-pill${resolved.error ? ' is-missing' : ''}`} title={resolved.error ?? resolved.mode}>
                {resolved.asset?.name || reference.name || reference.entityId}
              </span>
            )
          })}
        </span>
      )}
      <span className="df-node-foot">
        <span className="df-mono">{recordStatus(node)}</span>
        <span className="df-mono" title={node.recordId}>{node.recordId.slice(0, 8)}</span>
      </span>
    </button>
  )
}
