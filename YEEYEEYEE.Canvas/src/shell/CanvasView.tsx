import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { recordReferences, resolveReference, type Asset } from '../assets'
import { ALL_CHAPTERS_ID } from '../ChapterView'
import { entityThumbUrl } from './assetImages'
import type { LayoutMove } from './layoutPlan'
import { describeLease, isMine, nodeLease, treeLease, type Lease } from './locks'
import {
  canvasBounds, chapterGroups, isEditableRecord, kindOf, NODE_KINDS, nodeX, nodeY,
  recordContent, recordStatus, recordTitle, visibleEdges, type ShellEdge, type ViewRecord
} from './records'
import { gestureHint } from './uiText'

/**
 * 无限画布视图：按节点在画布上的**真实坐标**摆放。
 *
 * 桌面端的 CanvasSurface 是 3103 行命令式代码（一行行 new Border/TextBlock 搭卡片、
 * 自己维护指针状态机、自己量卡片高度去对齐连线端点）。这里不照抄那套结构——
 * 其中相当一部分是在为 Avalonia 的约束付代价（卡片 MinHeight、高度要等布局跑完才知道、
 * 世界画布要固定 4200×3000 才不被压缩）。DOM 里这些约束不存在：
 * 卡片用普通流式布局，缩放平移交给一条 transform。
 *
 * 如实说明，不假装有：
 *   - 连线**能读也能改了**：服务端投影连线（只投影两端都还在的），画布画得出来，
 *     新建与断开也接上了（新建走「连接模式」：点起点 → 点终点，见下面的 connectFrom）；
 *   - 网页端的写路径是「改标题与内容 / 移动单个节点 / 新建与删除节点 / 新建与删除连线 / 整理布局」，
 *     改类别还没有。
 */

export type Pan = { x: number; y: number }

// 画布上的手势提示（底部那一条）走两端共读的共享文案：措辞与分隔符都在 uiText.json 里，
// 桌面端嵌的是同一个文件。以前两端各写一份，措辞与分隔符已经走散过。

/**
 * 一次拖动的现场。放在 ref 里而不是 state：指针每动一下它都在变，但**只有落点需要重画**——
 * 把整个现场放进 state 会让每次移动都多一次无用的重渲染。
 */
type DragState = {
  pointerId: number
  recordId: string
  originX: number
  originY: number
  /** 按下时的屏幕坐标：位移要在屏幕空间量，再除以缩放换成世界坐标。 */
  startX: number
  startY: number
  /** 当前落点（世界坐标），阈值没到之前就等于起点。 */
  x: number
  y: number
  moved: boolean
}

/** 从卡片右缘的圆点拖一根线出来的现场。坐标都是世界坐标。 */
type ConnectDrag = { pointerId: number; sourceId: string; fromX: number; fromY: number; toX: number; toY: number }

/** 端口那几个处理器只用到坐标与指针捕获，元素是 button 还是 span 无所谓。 */
type PortPointerEvent = React.PointerEvent<HTMLElement>

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
  /** 通道自己的问题（编辑锁读不到、推送断开）。它不该让画布变得不可用，但必须说出来。 */
  channelNotice?: string
  /** 当前账号的 ID：用来区分「你在编辑」与「别人在编辑」。 */
  myUserId?: string
  /** 整理布局的虚影。只画不动——桌面端的预览层同样不参与命中测试。 */
  ghosts?: LayoutMove[]
  /** 画布上的连线（服务端只投影两端都还在的那些）。 */
  edges?: ShellEdge[]
  /**
   * 连接模式下的起点节点。非空时，点**另一个**节点是「连到它」而不是「选中它」。
   * 起点自己那张卡点了仍是选中——自环在入口就被拒，没必要让第二次点击白跑一趟 HTTP。
   */
  connectFrom?: string | null
  /** 连接模式下点了终点节点。只有连接模式会调它。 */
  onConnectTarget?: (recordId: string) => void
  /** 现在能不能拖节点（角色可编辑、画布不是只读）。单个节点还会再看锁与连接模式。 */
  draggable?: boolean
  /**
   * 拖动结束：把落点交出去（世界坐标）。**只有真的移动过才调**——
   * 差一点点的那一下是点击，不该写盘。位置由调用方负责落库，画布只画。
   */
  onMove?: (recordId: string, x: number, y: number) => void
  /**
   * 从卡片右缘的圆点拖到另一张卡片上松手：请求把两者连起来。
   * 与工具栏「连接」那条是**同一个动作**（同一个写路径、同一份规则），只是手势不同。
   */
  onConnectNodes?: (sourceId: string, targetId: string) => void
  /**
   * 右键一张节点卡：把「哪一张 + 屏幕坐标」交出去，菜单由调用方摆。
   * 画布不认识菜单的内容——*有哪些选项*是从服务端那份协助计划来的，画布只管把事件递出去。
   */
  onNodeContextMenu?: (recordId: string, position: { x: number; y: number }) => void
}

const MIN_ZOOM = 0.25
const MAX_ZOOM = 2.2

export function clampZoom(value: number): number {
  return Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, value))
}

/**
 * 拖动节点的起手阈值：世界坐标下 |dx| + |dy| 小于它，就还是一次**点击**。
 * 与桌面端同一个数（那边是 `Math.Abs(dx) + Math.Abs(dy) < 3`）——同一个手势在两端
 * 判成不同的事，是最难被发现的那种不一致。
 */
export const DRAG_THRESHOLD = 3

/**
 * 一次拖动的落点。规则与桌面端一致：不小于 0（画布左上角就是原点）。
 *
 * 抽成纯函数是为了能单独钉住两件容易写错的事：差一点点要算点击而不是拖动（否则每点一下节点都会
 * 被当成「挪了 0 像素」写一次盘），以及往左 / 往上拖会被夹在 0（否则节点能被拖到画布外面去）。
 */
export function dragDrop(
  origin: { x: number; y: number },
  dx: number,
  dy: number
): { x: number; y: number; moved: boolean } {
  return {
    x: Math.max(0, origin.x + dx),
    y: Math.max(0, origin.y + dy),
    moved: Math.abs(dx) + Math.abs(dy) >= DRAG_THRESHOLD
  }
}

/**
 * 从起点拖出一根线，松手时该连到谁。
 *
 * 落在自己身上（或压根没落在任何卡片上）就当没连过——**自环在入口就该被拒**，
 * 不必等一次 HTTP 换回服务端那句「不能连到自身」。服务端仍然会再判一次（那是权威）。
 */
export function connectDropTarget(sourceId: string, hitRecordId: string | null): string | null {
  return hitRecordId !== null && hitRecordId.length > 0 && hitRecordId !== sourceId ? hitRecordId : null
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
const EMPTY_EDGES: ShellEdge[] = []

// 箭头标记的 id 要在一页里唯一：同一个 id 出现两次时浏览器只认第一份，
// 第二张画布上的箭头就会跟着第一张的配色走。用自增序号而不是写死的常量。
let edgeArrowSeq = 0

export function CanvasView(props: CanvasViewProps) {
  const { records, selectedId, activeChapter, assets, assetsReady, showReferencePreviews, zoom, pan, onZoom, onPan, onSelect, apiRef } = props
  const leases = props.leases ?? EMPTY_LEASES
  const ghosts = props.ghosts ?? EMPTY_GHOSTS
  const [arrowId] = useState(() => `df-edge-arrow-${++edgeArrowSeq}`)
  const viewportRef = useRef<HTMLDivElement>(null)
  const panning = useRef<{ pointerId: number; startX: number; startY: number; originX: number; originY: number } | null>(null)

  /** 拖动中的实时落点（世界坐标）。null = 没在拖。 */
  const [drag, setDrag] = useState<{ recordId: string; x: number; y: number } | null>(null)
  const dragState = useRef<DragState | null>(null)
  /** 松手之后紧跟的那次 click 要吃掉：否则一次拖动会顺带把节点选中（还可能弹一次未保存确认）。 */
  const swallowClick = useRef(false)
  /** 正从哪张卡的右缘圆点拖一根线出来（null = 没在拖）。 */
  const [connectDrag, setConnectDrag] = useState<ConnectDrag | null>(null)
  const connectDragState = useRef<ConnectDrag | null>(null)

  /**
   * 拖动中的**预览**：把正在拖的那个节点的坐标换成手上的实时位置，其余原样。
   *
   * 卡片与连线都从这一份算（而不是给卡片单独套一个 CSS 位移），连线才会跟着节点走——
   * 桌面端拖动时也是这么做的。松手之后这份预览就没了，位置交给服务端回来的那一份。
   */
  const shown = useMemo(() => {
    if (!drag) return records
    return records.map((record) => (record.recordId === drag.recordId
      ? { ...record, record: { ...record.record, x: drag.x, y: drag.y } }
      : record))
  }, [records, drag])

  // 以下都从 `shown` 算，不是从 `records`：拖动中的那一份预览要同时管到卡片与连线。
  const bounds = useMemo(() => canvasBounds(shown), [shown])
  const nodes = useMemo(() => shown.filter(isEditableRecord), [shown])
  const movingIds = useMemo(() => new Set(ghosts.map((ghost) => ghost.recordId)), [ghosts])
  const others = useMemo(() => leases.filter((lease) => !isMine(lease, props.myUserId)), [leases, props.myUserId])

  /**
   * 这个节点现在能不能拖。三道闸门，与「能不能改它的文字」是同一套：
   * 角色可编辑（draggable）→ 没在连接模式（那时点卡片是「选终点」）→ 没有别人占着它、也没人占着整棵树。
   * 「别人占着」这一条只做在界面上（服务端的记录级写入本来就不仲裁锁），与「改标题内容」那条路一致。
   */
  function canDragNode(recordId: string): boolean {
    if (props.draggable !== true || !props.onMove) return false
    if (props.connectFrom) return false
    if (treeLease(others)) return false
    return nodeLease(others, recordId) === null
  }

  /**
   * 按下卡片：先记现场，指针捕获在**卡片自己**身上。
   * DOM 里的卡片不会像桌面端那样在按下时被重建，所以捕获挂在它身上就能一直收到后续事件
   * （桌面端必须捕获在画布上，那是 Avalonia 的约束，不是这里的）。
   */
  function beginCardDrag(event: React.PointerEvent<HTMLButtonElement>, record: ViewRecord) {
    if (event.button !== 0 || !canDragNode(record.recordId)) return
    const originX = nodeX(record)
    const originY = nodeY(record)
    dragState.current = {
      pointerId: event.pointerId,
      recordId: record.recordId,
      originX,
      originY,
      startX: event.clientX,
      startY: event.clientY,
      x: originX,
      y: originY,
      moved: false
    }
    event.currentTarget.setPointerCapture(event.pointerId)
  }

  function moveCardDrag(event: React.PointerEvent<HTMLButtonElement>) {
    const state = dragState.current
    if (!state || state.pointerId !== event.pointerId) return
    // 屏幕位移除以缩放才是世界位移：不除的话放大到 220% 时拖一格会走两倍多的距离。
    const drop = dragDrop(
      { x: state.originX, y: state.originY },
      (event.clientX - state.startX) / zoom,
      (event.clientY - state.startY) / zoom
    )
    state.x = drop.x
    state.y = drop.y
    // 阈值没到之前不认作拖动，也不重画：那一下还是「点击」。
    if (!drop.moved) return
    state.moved = true
    setDrag({ recordId: state.recordId, x: drop.x, y: drop.y })
  }

  /** 松手：真的移动过就交出去；中途被取消（比如系统弹了别的东西）就什么都不做。 */
  function endCardDrag(event: React.PointerEvent<HTMLButtonElement>, cancelled: boolean) {
    const state = dragState.current
    if (!state || state.pointerId !== event.pointerId) return
    dragState.current = null
    setDrag(null)
    if (event.currentTarget.hasPointerCapture(event.pointerId))
      event.currentTarget.releasePointerCapture(event.pointerId)
    if (!state.moved || cancelled) return
    swallowClick.current = true
    props.onMove?.(state.recordId, state.x, state.y)
  }

  /**
   * 能不能从这张卡拉线出去。与「能不能拖它」同一套闸门，只多一条：
   * 已经在连接模式里时不做（那时点卡片就是「选终点」，再叠一个拖线手势只会让人点错）。
   */
  function canStartConnect(recordId: string): boolean {
    if (props.draggable !== true || !props.onConnectNodes) return false
    if (props.connectFrom) return false
    if (treeLease(others)) return false
    return nodeLease(others, recordId) === null
  }

  /** 按住右缘的圆点：线从卡片右缘中点出发，与桌面端端口起手的位置一致。 */
  function beginPortDrag(event: PortPointerEvent, record: ViewRecord) {
    if (event.button !== 0 || !canStartConnect(record.recordId)) return
    const fromX = nodeX(record) + NODE_CARD_WIDTH
    const fromY = nodeY(record) + NODE_CARD_HEIGHT / 2
    const state: ConnectDrag = { pointerId: event.pointerId, sourceId: record.recordId, fromX, fromY, toX: fromX, toY: fromY }
    connectDragState.current = state
    setConnectDrag(state)
    event.currentTarget.setPointerCapture(event.pointerId)
  }

  function movePortDrag(event: PortPointerEvent) {
    const state = connectDragState.current
    const element = viewportRef.current
    if (!state || state.pointerId !== event.pointerId || !element) return
    const rect = element.getBoundingClientRect()
    const next = {
      ...state,
      toX: (event.clientX - rect.left - pan.x) / zoom,
      toY: (event.clientY - rect.top - pan.y) / zoom
    }
    connectDragState.current = next
    setConnectDrag(next)
  }

  function endPortDrag(event: PortPointerEvent, cancelled: boolean) {
    const state = connectDragState.current
    if (!state || state.pointerId !== event.pointerId) return
    connectDragState.current = null
    setConnectDrag(null)
    if (event.currentTarget.hasPointerCapture(event.pointerId))
      event.currentTarget.releasePointerCapture(event.pointerId)
    if (cancelled) return
    // 落点用命中测试问「指针底下是哪张卡」，不自己算几何：卡片高度是内容撑出来的，
    // 按矩形自己算迟早会和真实布局对不上。虚影层是 pointer-events:none，所以它不会被命中。
    const hit = document.elementFromPoint(event.clientX, event.clientY)?.closest('[data-record-id]')
    const target = connectDropTarget(state.sourceId, hit?.getAttribute('data-record-id') ?? null)
    if (target) props.onConnectNodes?.(state.sourceId, target)
  }

  /** 卡片点进来先问这一句：刚拖完的那一下要吃掉（顺带把标记消费掉，只吃一次）。 */
  function consumeClick(): boolean {
    if (!swallowClick.current) return false
    swallowClick.current = false
    return true
  }

  /**
   * 连线的**端点**：取卡片中心，与虚影连线同一套算法、同一份尺寸常量。
   *
   * 终点往回收半个卡片宽：这一层画在卡片**下面**，落在卡片里的箭头等于没画。
   * 这是个近似（卡片是方的，按方向往回让），但泳道布局的连线基本都是横的，
   * 近似结果与真实卡片边缘只差几个像素——够用，也不必为此把几何算成矩形求交。
   */
  const lines = useMemo(() => {
    const drawn = visibleEdges(props.edges ?? EMPTY_EDGES, shown)
    if (drawn.length === 0) return []
    const byId = new Map(nodes.map((node) => [node.recordId, node]))
    return drawn.flatMap((edge) => {
      const from = byId.get(edge.sourceId)
      const to = byId.get(edge.targetId)
      if (!from || !to) return []
      const dx = nodeX(to) - nodeX(from)
      const dy = nodeY(to) - nodeY(from)
      const span = Math.hypot(dx, dy)
      const back = span > 0 ? Math.min(span, NODE_CARD_WIDTH / 2 + 6) : 0
      return [{
        edgeId: edge.edgeId,
        x1: nodeX(from) + NODE_CARD_WIDTH / 2,
        y1: nodeY(from) + NODE_CARD_HEIGHT / 2,
        x2: nodeX(to) + NODE_CARD_WIDTH / 2 - (span > 0 ? (dx / span) * back : 0),
        y2: nodeY(to) + NODE_CARD_HEIGHT / 2 - (span > 0 ? (dy / span) * back : 0)
      }]
    })
  }, [props.edges, shown, nodes])

  /** 画布左上角那句「谁在编辑」。自己的锁不写在这里——自己当然知道自己刚点了什么。 */
  const whoLines = useMemo(() => {
    const lines: string[] = []
    const tree = treeLease(others)
    if (tree) lines.push(`${describeLease(tree)} 正在整理整棵树`)
    const byNode = others.filter((lease) => lease.scope === 'node')
    if (byNode.length > 0) {
      const named = byNode.map((lease) => {
        const target = shown.find((record) => record.recordId === lease.targetId)
        return `${describeLease(lease)} 编辑「${target ? recordTitle(target) : '这个节点'}」`
      })
      lines.push(named.slice(0, 2).join('；') + (named.length > 2 ? ` 等 ${named.length} 处` : ''))
    }
    return lines
  }, [others, shown])

  /**
   * 「只看这一章」压暗的是**不在这一组里**的节点。
   * 用分组结果反推成员，而不是拿 chapterId 直接比：企划组与未归档组根本没有一个能比的 chapterId，
   * 直接比会把这两组永远压暗（点了没反应）。
   */
  const activeIds = useMemo(() => {
    if (activeChapter === ALL_CHAPTERS_ID) return null
    const group = chapterGroups(shown).find((item) => item.id === activeChapter)
    return new Set(group ? group.records.map((item) => item.recordId) : [])
  }, [shown, activeChapter])

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
      {/* 画布上不给浏览器自己那份菜单（后退 / 查看源代码）：这里要的是节点右键菜单。 */}
      <div className="df-viewport" ref={viewportRef} onContextMenu={(event) => event.preventDefault()}>
        <div className="df-world" style={{ width: bounds.width, height: bounds.height, transform: `translate(${pan.x}px, ${pan.y}px) scale(${zoom})` }}>
          <div className="df-world-grid" />
          {/* 连线层：铺在网格之上、卡片之下。整层不可点——线不参与命中测试，
              点在线上等于点在空白处（与桌面端一致）。 */}
          {lines.length > 0 && (
            <svg className="df-edge-layer" width={bounds.width} height={bounds.height} aria-hidden="true">
              <defs>
                <marker
                  id={arrowId}
                  viewBox="0 0 8 8"
                  refX="7"
                  refY="4"
                  markerWidth="6"
                  markerHeight="6"
                  orient="auto"
                >
                  <path d="M0,0 L8,4 L0,8 Z" />
                </marker>
              </defs>
              {lines.map((line) => (
                <line
                  key={line.edgeId}
                  x1={line.x1}
                  y1={line.y1}
                  x2={line.x2}
                  y2={line.y2}
                  markerEnd={`url(#${arrowId})`}
                />
              ))}
            </svg>
          )}
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
              connectFrom={props.connectFrom ?? null}
              onConnectTarget={props.onConnectTarget}
              draggable={canDragNode(node.recordId)}
              dragging={drag?.recordId === node.recordId}
              onDragStart={(event) => beginCardDrag(event, node)}
              onDragMove={moveCardDrag}
              onDragEnd={endCardDrag}
              consumeClick={consumeClick}
              showPort={canStartConnect(node.recordId)}
              onPortStart={(event) => beginPortDrag(event, node)}
              onPortMove={movePortDrag}
              onPortEnd={endPortDrag}
              onNodeContextMenu={props.onNodeContextMenu}
            />
          ))}

          {/* 拖线时的虚影：画在卡片**上面**——线尖跟着指针走，压在卡片下面时线尖会被卡片吃掉。整层不可点。 */}
          {connectDrag && (
            <svg className="df-connect-layer" width={bounds.width} height={bounds.height} aria-hidden="true">
              <line x1={connectDrag.fromX} y1={connectDrag.fromY} x2={connectDrag.toX} y2={connectDrag.toY} />
            </svg>
          )}

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
        {(whoLines.length > 0 || props.channelNotice) && (
          <div className="df-who">
            {whoLines.map((line) => <span key={line}>◉ {line}</span>)}
            {props.channelNotice && <span>◉ {props.channelNotice}</span>}
          </div>
        )}
      </div>
      {/* 提示条跟着当前手势走：连接模式下必须说清「下一次点击是干什么的」，
          否则点下去凭空多一根线，比没有这个功能更让人困惑。
          每一句措辞与分隔符都走**共享文案**（uiText.json，桌面端嵌的是同一份文件）：
          以前两端各写一份，措辞与分隔符已经走散过。
          **列哪几个手势两端各定**：桌面端末尾还有「Delete 删除」，网页端的删除在检查器与工作树上，不挂快捷键。 */}
      <div className="df-hint">
        {props.connectFrom
          ? gestureHint('connect.mode', 'gesture.cancel')
          : gestureHint('gesture.pan', 'gesture.zoom', 'gesture.dragNode', 'gesture.connect', 'gesture.select')}
      </div>
    </div>
  )
}

function NodeCard({ node, selected, dimmed, moving, lock, myUserId, assets, assetsReady, showReferences, onSelect, connectFrom, onConnectTarget, draggable, dragging, onDragStart, onDragMove, onDragEnd, consumeClick, showPort, onPortStart, onPortMove, onPortEnd, onNodeContextMenu }: {
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
  connectFrom: string | null
  onConnectTarget?: (recordId: string) => void
  /** 这张卡现在能不能拖。不能拖时连指针事件也不接，免得按下去半天没反应。 */
  draggable: boolean
  /** 正在被拖：抬起来画（压过别的卡），并让连线跟着走。 */
  dragging: boolean
  onDragStart: (event: React.PointerEvent<HTMLButtonElement>) => void
  onDragMove: (event: React.PointerEvent<HTMLButtonElement>) => void
  onDragEnd: (event: React.PointerEvent<HTMLButtonElement>, cancelled: boolean) => void
  /** 这次点击是不是刚拖完的那一下（是就得吃掉）。 */
  consumeClick: () => boolean
  /** 右缘那个连线圆点给不给（能不能从这张卡拉线出去）。 */
  showPort: boolean
  onPortStart: (event: PortPointerEvent) => void
  onPortMove: (event: PortPointerEvent) => void
  onPortEnd: (event: PortPointerEvent, cancelled: boolean) => void
  /** 右键这张卡：把「哪一张 + 屏幕坐标」交出去，菜单由调用方摆。 */
  onNodeContextMenu?: (recordId: string, position: { x: number; y: number }) => void
}) {
  const kind = kindOf(node.recordType)
  const meta = NODE_KINDS[kind]
  const references = recordReferences(node)
  const mine = isMine(lock, myUserId)
  const connecting = connectFrom === node.recordId
  /** 连接模式下，除了起点自己，每张卡都是可选的终点——点它就是画线。 */
  const armed = connectFrom !== null && !connecting && !!onConnectTarget
  return (
    <button
      type="button"
      data-record-id={node.recordId}
      className={`df-node${selected ? ' is-selected' : ''}${moving ? ' is-moving' : ''}${connecting ? ' is-connecting' : ''}${armed ? ' is-connectable' : ''}${draggable && !armed && !connecting ? ' is-draggable' : ''}${dragging ? ' is-dragging' : ''}`}
      style={{ left: nodeX(node), top: nodeY(node), width: NODE_CARD_WIDTH, minHeight: NODE_CARD_HEIGHT, opacity: dimmed ? 0.35 : moving ? 0.4 : 1 }}
      onPointerDown={(event) => {
        event.stopPropagation()
        onDragStart(event)
      }}
      onPointerMove={onDragMove}
      // 松手与取消各走一次同一个收尾：取消时不该把半截位置落库。
      onPointerUp={(event) => onDragEnd(event, false)}
      onPointerCancel={(event) => onDragEnd(event, true)}
      onClick={(event) => {
        event.stopPropagation()
        // 刚拖完的那一下吃掉：选中与「放到哪儿」是两件事，不该被一次拖动一起触发。
        if (consumeClick()) return
        if (armed) { onConnectTarget!(node.recordId); return }
        onSelect(node.recordId)
      }}
      onContextMenu={(event) => {
        // 右键先**选中**再出菜单：菜单里那几项（编辑 / 删除）作用的是「这一张」，
        // 不选中的话，第 2 秒看到的就是「右侧检查器还停在上一张」。
        event.preventDefault()
        event.stopPropagation()
        onSelect(node.recordId)
        onNodeContextMenu?.(node.recordId, { x: event.clientX, y: event.clientY })
      }}
      title={`${meta.label} · ${recordStatus(node)}${lock ? ` · ${describeLease(lock)} 正在编辑` : ''}${armed ? ' · 点它连到这里' : draggable ? ' · 按住可拖动' : ''}`}
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
                {/* 引用的那一版设定长什么样，卡片上直接看得见。取不到图就把 img 收起来——
                    缩略图是**加成**，缺了它这条引用仍然是可读的一枚徽标，不该变成一个破图图标。 */}
                {reference.entityId && (
                  <img
                    className="df-ref-thumb"
                    src={entityThumbUrl(reference.entityId, reference.variantId, reference.variantVersionId)}
                    alt=""
                    loading="lazy"
                    onError={(event) => { event.currentTarget.style.display = 'none' }}
                  />
                )}
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
      {/* 右缘的连线圆点。按住它拖到另一张卡上就是连线——与工具栏「连接」同一个动作，只是手势不同。
          按下时先 stopPropagation：否则卡片自己那套「按住改位置」会先接手。 */}
      {showPort && (
        <span
          className="df-port"
          role="presentation"
          title="按住这个圆点拖到另一个节点就能连线"
          onPointerDown={(event) => { event.stopPropagation(); onPortStart(event) }}
          onPointerMove={onPortMove}
          onPointerUp={(event) => onPortEnd(event, false)}
          onPointerCancel={(event) => onPortEnd(event, true)}
        />
      )}
    </button>
  )
}
