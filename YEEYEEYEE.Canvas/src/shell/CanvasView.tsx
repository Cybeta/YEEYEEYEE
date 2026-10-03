import { useCallback, useEffect, useMemo, useRef } from 'react'
import { recordReferences, resolveReference, type Asset } from '../assets'
import { ALL_CHAPTERS_ID } from '../ChapterView'
import type { LayoutMove } from './layoutPlan'
import { describeLease, isMine, nodeLease, treeLease, type Lease } from './locks'
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
  /** 当前有效的编辑锁：卡片上挂徽标，画布左上角说「谁在编辑」。 */
  leases?: Lease[]
  /** 锁这一路自己的问题（读不到、有条目读不懂）。它不该让画布变得不可用，但必须说出来。 */
  leaseNotice?: string
  /** 当前账号的 ID：用来区分「你在编辑」与「别人在编辑」。 */
  myUserId?: string
  /** 整理布局的虚影。只画不动——桌面端的预览层同样不参与命中测试。 */
  ghosts?: LayoutMove[]
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

/**
 * 节点卡的尺寸。**这里是唯一一份**：真实卡片与虚影卡都用它铺样式，
 * 虚影连线的端点也算在卡片中心——所以不必担心 CSS 里那份悄悄改了而这里没跟上。
 * （桌面端也是这个做法：NodeWidth / NodeHeight 两个常量驱动卡片。）
 */
const NODE_CARD_WIDTH = 232
const NODE_CARD_HEIGHT = 104

// 空默认值放在模块级：写成 `?? []` 会让 useMemo 每次拿到新数组，白白重算一遍。
const EMPTY_LEASES: Lease[] = []
const EMPTY_GHOSTS: LayoutMove[] = []

export function CanvasView(props: CanvasViewProps) {
  const { records, selectedId, activeChapter, assets, assetsReady, showReferencePreviews, zoom, pan, onZoom, onPan, onSelect, apiRef } = props
  const leases = props.leases ?? EMPTY_LEASES
  const ghosts = props.ghosts ?? EMPTY_GHOSTS
  const viewportRef = useRef<HTMLDivElement>(null)
  const panning = useRef<{ pointerId: number; startX: number; startY: number; originX: number; originY: number } | null>(null)

  const bounds = useMemo(() => canvasBounds(records), [records])
  const nodes = useMemo(() => records.filter(isEditableRecord), [records])
  const movingIds = useMemo(() => new Set(ghosts.map((ghost) => ghost.recordId)), [ghosts])
  const others = useMemo(() => leases.filter((lease) => !isMine(lease, props.myUserId)), [leases, props.myUserId])

  /** 画布左上角那句「谁在编辑」。自己的锁不写在这里——自己当然知道自己刚点了什么。 */
  const whoLines = useMemo(() => {
    const lines: string[] = []
    const tree = treeLease(others)
    if (tree) lines.push(`${describeLease(tree)} 正在整理整棵树`)
    const byNode = others.filter((lease) => lease.scope === 'node')
    if (byNode.length > 0) {
      const named = byNode.map((lease) => {
        const target = records.find((record) => record.recordId === lease.targetId)
        return `${describeLease(lease)} 编辑「${target ? recordTitle(target) : '这个节点'}」`
      })
      lines.push(named.slice(0, 2).join('；') + (named.length > 2 ? ` 等 ${named.length} 处` : ''))
    }
    return lines
  }, [others, records])

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
              moving={movingIds.has(node.recordId)}
              lock={nodeLease(leases, node.recordId)}
              myUserId={props.myUserId}
              assets={assets}
              assetsReady={assetsReady}
              showReferences={showReferencePreviews}
              onSelect={onSelect}
            />
          ))}

          {/* 整理布局的虚影：目标位置的虚线卡 + 从原位置过去的虚线。整层不可点。 */}
          {ghosts.length > 0 && (
            <div className="df-ghost-layer" aria-hidden="true">
              <svg className="df-ghost-lines" width={bounds.width} height={bounds.height}>
                {ghosts.map((ghost) => (
                  <line
                    key={ghost.recordId}
                    x1={ghost.from.x + NODE_CARD_WIDTH / 2}
                    y1={ghost.from.y + NODE_CARD_HEIGHT / 2}
                    x2={ghost.to.x + NODE_CARD_WIDTH / 2}
                    y2={ghost.to.y + NODE_CARD_HEIGHT / 2}
                  />
                ))}
              </svg>
              {ghosts.map((ghost) => (
                <div
                  key={ghost.recordId}
                  className="df-ghost-card"
                  style={{ left: ghost.to.x, top: ghost.to.y, width: NODE_CARD_WIDTH, minHeight: NODE_CARD_HEIGHT }}
                >
                  <span className="df-node-title">{ghost.title || '未命名节点'}</span>
                  <span className="df-ghost-coords df-mono">整理后 {Math.round(ghost.to.x)}, {Math.round(ghost.to.y)}</span>
                </div>
              ))}
            </div>
          )}
        </div>
        <div className="df-scanband" aria-hidden="true" />
        {(whoLines.length > 0 || props.leaseNotice) && (
          <div className="df-who">
            {whoLines.map((line) => <span key={line}>◉ {line}</span>)}
            {props.leaseNotice && <span>◉ {props.leaseNotice}</span>}
          </div>
        )}
      </div>
      {/* 桌面端那句提示末尾还有「Delete 删除」；网页端没有删除接口，所以不写上去。 */}
      <div className="df-hint">拖拽平移 · 滚轮缩放 · 点击节点选中</div>
    </div>
  )
}

function NodeCard({ node, selected, dimmed, moving, lock, myUserId, assets, assetsReady, showReferences, onSelect }: {
  node: ViewRecord
  selected: boolean
  dimmed: boolean
  moving: boolean
  lock: Lease | null
  myUserId?: string
  assets: Asset[]
  assetsReady: boolean
  showReferences: boolean
  onSelect: (recordId: string) => void
}) {
  const kind = kindOf(node.recordType)
  const meta = NODE_KINDS[kind]
  const references = recordReferences(node)
  const mine = isMine(lock, myUserId)
  return (
    <button
      type="button"
      className={`df-node${selected ? ' is-selected' : ''}${moving ? ' is-moving' : ''}`}
      style={{ left: nodeX(node), top: nodeY(node), width: NODE_CARD_WIDTH, minHeight: NODE_CARD_HEIGHT, opacity: dimmed ? 0.35 : moving ? 0.4 : 1 }}
      onPointerDown={(event) => event.stopPropagation()}
      onClick={(event) => { event.stopPropagation(); onSelect(node.recordId) }}
      title={`${meta.label} · ${recordStatus(node)}${lock ? ` · ${describeLease(lock)} 正在编辑` : ''}`}
    >
      <span className="df-node-kind">
        <span className="df-node-dot" style={{ background: meta.hex }} aria-hidden="true" />
        <span>{meta.label}</span>
      </span>
      <span className="df-node-title">{recordTitle(node)}</span>
      <span className="df-node-body">{recordContent(node) || '暂无内容'}</span>
      {/* 别人正在编辑这个节点：徽标挂在卡片上，不用点开检查器才知道。
          自己握着的时候也要显示——但说的不是「有人占着」，而是「你在编辑」，
          这两句在界面上是两件事，颜色也分开。 */}
      {lock && (
        <span className={`df-lock-pill${mine ? ' is-mine' : ''}`}>
          {mine ? '◉ 你在编辑' : `◉ ${describeLease(lock)} 正在编辑`}
        </span>
      )}
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
