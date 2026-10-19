import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react'
import { recordAttachments, recordReferences, resolveReference, type Asset, type Reference } from '../assets'
import { ALL_CHAPTERS_ID } from '../ChapterView'
import { agentPreviewLayer, describeNodeAssetDelta, type AgentCanvasPreview } from './agentPreview'
import './agentPreview.css'
import { attachmentFileUrl, entityThumbUrl } from './assetImages'
import type { LayoutMove } from './layoutPlan'
import { describeLease, isMine, nodeLease, treeLease, type Lease } from './locks'
import { nodeJobState, nodeMedia, type NodeJob, type NodeJobState } from './nodeMedia'
import {
  CANDIDATE_CARD,
  candidateCardRects,
  type CandidateBatchView,
  type CandidateSlotView
} from './candidateBatch'
import { gachaBatchProgress } from './gachaRules'
import { qualityBadge, qualityIsCracked, type BatchQualityRun } from './imageQuality'
import {
  canvasBounds, chapterGroups, isEditableRecord, kindOf, NODE_KINDS, nodeStatusOf, nodeX, nodeY,
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
  /**
   * 当前的生成任务（`GET /api/web/jobs`）。卡片据此显示「生成中 / 失败」。
   * **按任务的 targetNodeId 归到节点上**，不看当前选中项：选中项换了，任务仍属于它的原目标节点。
   */
  jobs?: NodeJob[]
  /**
   * 节点出图的候选批次（临时卡片，见 candidateBatch.ts）。它们**不是**画布节点：
   * 不进正式画布数据、不参与连线、不参与命中测试的节点层。
   */
  candidates?: CandidateBatchView[]
  /** 开奖模式：候选区先只露卡背与任务进度，出好的图也要等用户点「开奖」才揭晓。
   * 关掉时保持原来的候选卡行为（直接看缩略图、逐张采用）。
   */
  gachaReveal?: boolean
  /** 判档开关：关着时不摆档位，候选卡写「未判档」。 */
  showQuality?: boolean
  /** 每批判档的运行状态（正在判 / 模型不可用 / 失败 / 已跳过）。 */
  qualityRuns?: Record<string, BatchQualityRun>
  /** 用户明确跳过这一批判档：只改判档状态，不影响查看与采用。 */
  onSkipQuality?: (batchId: string, jobIds: string[]) => void
  /** 用户明确重试这一批判档（不会因为页面重渲染自动重试）。 */
  onRetryQuality?: (batchId: string, jobIds: string[]) => void
  /** 主动点「开奖」：整批落终态之后才允许，打开的是画布之外的全屏层。 */
  onRevealCandidates?: (batchId: string) => void
  /** 采用某一件候选产物：由服务端校验归属、目标、权限、租约与锁定之后追加到原节点。 */
  onAdoptCandidate?: (nodeId: string, jobId: string, outputIndex: number) => void
  /** 丢弃某一批候选（只撤掉卡片；不会取消后台任务，也不会删产物文件）。 */
  onDiscardCandidates?: (batchId: string) => void
  /** 看候选产物的大图（浮层摆在画布之外，见 WebCanvasApp）。 */
  onOpenCandidate?: (jobId: string, outputIndex: number, name: string) => void
  agentPreview?: AgentCanvasPreview | null
  previewRecords?: ViewRecord[]
  previewMatches?: (record: ViewRecord) => boolean
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
  /**
   * 点了一张卡上的**引用徽标**：交给调用方去打开这条引用的详情。
   * 它只影响检查器的展开状态，不改引用配置、不改画布；也不等于「进引用画布」——
   * 网页端没有那套临时画布，普通实体引用不该被当成画布跳转。
   */
  onOpenReference?: (recordId: string, reference: Reference) => void
  /**
   * 点了一张卡上**这个节点自己出的图**：交给调用方去开大图（先选中这个节点）。
   * 卡片上的那一格是「产物缩略图」，看大图要铺满视口的浮层——画布世界整层带 transform，
   * 浮层挂在这里会变成相对世界定位，所以由调用方在画布之外摆。
   */
  onOpenMedia?: (recordId: string, attachmentId: string) => void
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
  const preview = useMemo(() => agentPreviewLayer(props.agentPreview, props.previewRecords ?? records,
    activeChapter, props.previewMatches), [props.agentPreview, props.previewRecords, records, activeChapter, props.previewMatches])
  const fitNodes = useMemo(() => [...shown.filter(isEditableRecord), ...preview.nodes.map(item => item.node)], [shown, preview])
  const bounds = useMemo(() => canvasBounds(fitNodes), [fitNodes])
  const nodes = useMemo(() => shown.filter(isEditableRecord), [shown])
  const movingIds = useMemo(() => new Set(ghosts.map((ghost) => ghost.recordId)), [ghosts])
  const others = useMemo(() => leases.filter((lease) => !isMine(lease, props.myUserId)), [leases, props.myUserId])

  /**
   * 每个节点身上的任务汇总，按任务的 targetNodeId 归组。
   *
   * 这一步就是「任务不串节点」的落点：任务归谁由它自己带着的目标节点说了算，
   * 与「现在选中了哪一张」完全无关——所以切换节点时这个映射一个字都不会变。
   */
  const jobStates = useMemo(() => {
    const grouped = new Map<string, NodeJob[]>()
    for (const job of props.jobs ?? []) {
      if (!job.targetNodeId) continue
      grouped.set(job.targetNodeId, [...(grouped.get(job.targetNodeId) ?? []), job])
    }
    const states = new Map<string, NodeJobState>()
    for (const [nodeId, list] of grouped) states.set(nodeId, nodeJobState(list, nodeId))
    return states
  }, [props.jobs])

  /**
   * 每个节点上有几件候选图「等着采用」。
   *
   * 用它把卡片状态写成「待确认」——**不能**提前写成「已完成」：产物还没进节点，
   * 采用之前节点上没有这件东西（Desktop 也是先 NeedsReview、采用时才 Completed）。
   */
  const adoptableCandidates = useMemo(() => {
    const counts = new Map<string, number>()
    for (const batch of props.candidates ?? []) {
      const pending = batch.slots.filter((slot) => slot.adoptable).length
      if (pending > 0) counts.set(batch.nodeId, (counts.get(batch.nodeId) ?? 0) + pending)
    }
    return counts
  }, [props.candidates])

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
    if (!fitNodes.length) {
      // 桌面端 ApplyFit 在空画布上的取值。
      onZoom(1)
      onPan({ x: 40, y: 40 })
      return
    }
    let minX = Number.POSITIVE_INFINITY
    let minY = Number.POSITIVE_INFINITY
    let maxX = Number.NEGATIVE_INFINITY
    let maxY = Number.NEGATIVE_INFINITY
    for (const node of fitNodes) {
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
  }, [fitNodes, onZoom, onPan])

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

  const previewFitKey = props.agentPreview
    ? JSON.stringify([props.agentPreview.sessionId, props.agentPreview.messageId, props.agentPreview.revision]) : null
  const fittedPreview = useRef<string | null>(null)
  useEffect(() => {
    if (previewFitKey !== null && fittedPreview.current !== previewFitKey) fit()
    fittedPreview.current = previewFitKey
  }, [previewFitKey, fit])

  useEffect(() => {
    const element = viewportRef.current
    if (!element) return
    const onPointerDown = (event: PointerEvent) => {
      if (event.button !== 0) return
      // 按在节点卡上不要开始平移：那是「选中/拖节点」的手势。原生监听在 viewport 这一层先于
      // React 的合成事件触发，所以 React 那边的 stopPropagation 拦不住它，只能在这里判。
      // 候选卡与它下面那行按钮同理：它们是画布上的临时控件，按下不能被当成「空白处平移」——
      // 一旦在这里捕获了指针，按钮就收不到 pointerup，click 永远不会发生。
      if ((event.target as HTMLElement | null)?.closest('.df-node, .df-candidate-layer')) return
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
              jobState={jobStates.get(node.recordId) ?? null}
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
              onOpenReference={props.onOpenReference}
              onOpenMedia={props.onOpenMedia}
              adoptableCandidates={adoptableCandidates.get(node.recordId) ?? 0}
            />
          ))}

          {/* 节点出图的候选卡：临时的、挂在原节点旁边（节点一动它们跟着动，见 candidateCardRects）。
              它不是画布节点——不进正式数据、不参与连线；点它上面的按钮也不会被当成拖节点或连线。 */}
          {(props.candidates ?? []).map((batch) => {
            const owner = nodes.find((node) => node.recordId === batch.nodeId)
            if (!owner) return null
            const rects = candidateCardRects(batch.slots.length, { x: nodeX(owner), y: nodeY(owner) }, NODE_CARD_WIDTH, NODE_CARD_HEIGHT)
            const progress = gachaBatchProgress(batch.slots)
            const qualityRun = props.qualityRuns?.[batch.batchId]
            const succeededJobIds = batch.slots
              .filter(slot => slot.status === 'succeeded' && slot.jobId.length > 0)
              .map(slot => slot.jobId)
            // 说明行摆在卡排外侧：卡排在上方时放它上面，避免压在节点卡上（按钮会被节点挡住）。
            const captionTop = Math.max(4, rects[0].top < nodeY(owner)
              ? rects[0].top - 22
              : rects[0].top + CANDIDATE_CARD.height + 4)
            return (
              <div key={batch.batchId} className="df-candidate-layer">
                {batch.slots.map((slot, index) => (
                  <CandidateCard
                    key={slot.key}
                    slot={slot}
                    position={rects[index]}
                    index={index}
                    startedAt={batch.startedAt}
                    nodeId={batch.nodeId}
                    faceDown={props.gachaReveal === true}
                    showQuality={props.showQuality === true}
                    onAdopt={props.onAdoptCandidate}
                    onOpen={props.onOpenCandidate}
                  />
                ))}
                <div className="df-candidate-caption" style={{ left: rects[0].left, top: captionTop }}>
                  <span>{batch.headline}</span>
                  {/* 判档状态只说明进度与问题，**不挡开奖**：模型不可用时也能先看图、先采用。 */}
                  {props.showQuality === true && qualityRun?.state === 'running' && (
                    <span className="df-candidate-caption-note">{qualityRun.message || '正在判档…（可以先开奖）'}</span>
                  )}
                  {props.showQuality === true && (qualityRun?.state === 'unavailable' || qualityRun?.state === 'failed') && (
                    <>
                      <span className="df-candidate-fail" title={qualityRun.message}>{qualityRun.message}</span>
                      <button type="button" className="df-candidate-action" onPointerDown={stopCanvasGesture}
                        title="本次跳过判档：不写档位，仍可查看与采用"
                        onClick={() => props.onSkipQuality?.(batch.batchId, succeededJobIds)}>跳过判档</button>
                      <button type="button" className="df-candidate-action" onPointerDown={stopCanvasGesture}
                        title="你明确要求再判一次（会再调用模型，可能产生费用）"
                        onClick={() => props.onRetryQuality?.(batch.batchId, succeededJobIds)}>重试判档</button>
                    </>
                  )}
                  {props.showQuality === true && qualityRun?.state === 'skipped' && (
                    <span className="df-candidate-caption-note">判档已跳过</span>
                  )}
                  {props.gachaReveal && progress.terminal && progress.canReveal && (
                    <button type="button" className="df-candidate-action is-primary" onPointerDown={stopCanvasGesture}
                      title="整批已经结束，点开进入揭晓" onClick={() => props.onRevealCandidates?.(batch.batchId)}>开奖</button>
                  )}
                  {props.gachaReveal && progress.allFailed && (
                    <span className="df-candidate-fail">全部失败，没有可开奖的图</span>
                  )}
                  {props.gachaReveal && !progress.terminal && (
                    <span className="df-candidate-caption-note">等待全部结束</span>
                  )}
                  {props.gachaReveal && progress.terminal && !progress.canReveal && !progress.allFailed && (
                    <span className="df-candidate-caption-note">没有待揭晓的图</span>
                  )}
                  <button type="button" className="df-candidate-action" onPointerDown={stopCanvasGesture}
                    onClick={() => props.onDiscardCandidates?.(batch.batchId)}>丢弃这一批</button>
                </div>
              </div>
            )
          })}

          {/* 拖线时的虚影：画在卡片**上面**——线尖跟着指针走，压在卡片下面时线尖会被卡片吃掉。整层不可点。 */}
          {connectDrag && (
            <svg className="df-connect-layer" width={bounds.width} height={bounds.height} aria-hidden="true">
              <line x1={connectDrag.fromX} y1={connectDrag.fromY} x2={connectDrag.toX} y2={connectDrag.toY} />
            </svg>
          )}

          {props.agentPreview && (
            <div className="df-agent-preview-layer" aria-label="Agent 待审批改动">
              <svg width={bounds.width} height={bounds.height}>
                <defs>
                  {(['added', 'removed'] as const).map(change => (
                    <marker key={change} id={`${arrowId}-preview-${change}`} className={`is-${change}`}
                      viewBox="0 0 8 8" refX="7" refY="4" markerWidth="6" markerHeight="6" orient="auto">
                      <path d="M0,0 L8,4 L0,8 Z" />
                    </marker>
                  ))}
                </defs>
                {preview.edges.map(({ change, edge, from, to }) => {
                  const x1 = nodeX(from) + NODE_CARD_WIDTH
                  const y1 = nodeY(from) + NODE_CARD_HEIGHT / 2
                  const x2 = nodeX(to)
                  const y2 = nodeY(to) + NODE_CARD_HEIGHT / 2
                  return (
                    <g key={`${change}-${edge.edgeId}`} className={`is-${change}`}>
                      <line x1={x1} y1={y1} x2={x2} y2={y2} markerEnd={`url(#${arrowId}-preview-${change})`} />
                      <text x={(x1 + x2) / 2} y={(y1 + y2) / 2 - 8}>{change === 'added' ? '＋ 新增连线' : '− 删除连线'}</text>
                    </g>
                  )
                })}
              </svg>
              {preview.nodes.map(({ change, node, assets }) => {
                // 这个节点上待生效的附件 / 引用变化：虚影里只给一行摘要，
                // 具体增删改在右侧 Agent 审批区展开（虚影层整层不可点，本来就放不下交互）。
                const assetLine = describeNodeAssetDelta(assets)
                return (
                  <div key={`${change}-${node.recordId}`} className={`df-agent-preview-card is-${change}`}
                    style={{ left: nodeX(node), top: nodeY(node), width: NODE_CARD_WIDTH, minHeight: NODE_CARD_HEIGHT }}>
                    <span className="df-agent-preview-label">{change === 'added' ? '＋ 将新增' : change === 'updated' ? '△ 将修改' : '− 将删除'} · 审批后生效</span>
                    <span className="df-node-title">{recordTitle(node)}</span>
                    <span className="df-node-body">{recordContent(node) || '暂无内容'}</span>
                    {assetLine && <span className="df-agent-preview-asset-summary">{assetLine}</span>}
                  </div>
                )
              })}
            </div>
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
        {props.agentPreview && (
          <div className="df-agent-preview-legend" role="status">
            <span>Agent 待审批预览</span>
            <span className="is-added">＋ 绿色：新增节点 / 连线</span>
            <span className="is-updated">△ 黄色：修改节点</span>
            <span className="is-removed">− 红色：删除节点 / 连线</span>
          </div>
        )}
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

function NodeCard({ node, selected, dimmed, moving, lock, myUserId, assets, assetsReady, showReferences, jobState, adoptableCandidates, onSelect, connectFrom, onConnectTarget, draggable, dragging, onDragStart, onDragMove, onDragEnd, consumeClick, showPort, onPortStart, onPortMove, onPortEnd, onNodeContextMenu, onOpenReference, onOpenMedia }: {
  node: ViewRecord
  selected: boolean
  dimmed: boolean
  moving: boolean
  lock: Lease | null
  myUserId?: string
  assets: Asset[]
  assetsReady: boolean
  showReferences: boolean
  /** 这个节点身上的生成任务（按 targetNodeId 归好的）。null = 没有任务。 */
  jobState: NodeJobState | null
  /** 这个节点上还有几件出好的候选图在等采用（0 = 没有）。 */
  adoptableCandidates: number
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
  /** 点这张卡上的引用徽标：打开那条引用的详情（不改任何数据）。 */
  onOpenReference?: (recordId: string, reference: Reference) => void
  /** 点这张卡上自己出的一张图：打开它的大图（不改任何数据）。没给就只是选中这张卡。 */
  onOpenMedia?: (recordId: string, attachmentId: string) => void
}) {
  const kind = kindOf(node.recordType)
  const meta = NODE_KINDS[kind]
  const references = recordReferences(node)
  // 这个节点**自己**的产物（不是它引用了谁）：图片与视频在上面那排引用徽标之外单独列，
  // 两者说的是两件事，混在一起就会把「引用的设定图」看成「这个节点出的图」。
  const media = nodeMedia(recordAttachments(node))
  const ownMedia = [...media.images, ...media.videos]
  const [brokenImages, setBrokenImages] = useState<string[]>([])
  /**
   * 点卡片上的一张产物图：先选中这个节点（右侧检查器跟着切过去），再打开它的大图。
   * 两件事在这里是同一个动作的两半；指针事件在这一格上先被吃掉（见下面 onPointerDown），
   * 所以拖动、连线、以及卡片自己那一下「点空白选中」都不会从这里起手。
   */
  function openOwnImage(event: ReactMouseEvent, attachmentId: string) {
    if (!onOpenMedia) return
    event.stopPropagation()
    onSelect(node.recordId)
    onOpenMedia(node.recordId, attachmentId)
  }
  const mine = isMine(lock, myUserId)
  const connecting = connectFrom === node.recordId
  /** 连接模式下，除了起点自己，每张卡都是可选的终点——点它就是画线。 */
  const armed = connectFrom !== null && !connecting && !!onConnectTarget
  // 节点自身的执行状态（服务端投影）。到了生成这一步，它还只反映「产物已经回挂」这种落定的事，
  // 所以下面再用**任务**把它顶掉：跑到哪一步、失败了没有，只有任务知道。
  const status = nodeStatusOf(recordStatus(node))
  const activeJobs = jobState?.active ?? []
  const failures = jobState?.failures ?? []
  // 三种覆盖，优先级就是这里的顺序：**正在跑的任务**最先（它是「现在」，别的都只是结果）；
  // 其次是「有候选图等着采用」——这时不能显示成已完成（产物还没进节点，参见 candidateBatch）；
  // 最后才是失败。同节点上有别的任务在跑时，本批候选不覆盖它。
  const live = activeJobs.length > 0
    ? {
        label: activeJobs.length > 1 ? `生成中 ${activeJobs.length} 项` : '生成中',
        hex: nodeStatusOf('Generating').hex,
        title: `这个节点上有 ${activeJobs.length} 个生成任务正在进行：${activeJobs.map((job) => job.jobId.slice(0, 8)).join('、')}`
      }
    : adoptableCandidates > 0
      ? {
          label: '待确认',
          hex: nodeStatusOf('NeedsReview').hex,
          title: `有 ${adoptableCandidates} 张候选图等着采用（采用之前不会写进这个节点）`
        }
      : failures.length > 0
        ? {
            label: '失败',
            hex: nodeStatusOf('Failed').hex,
            title: failures.map((failure) => `${failure.jobId.slice(0, 8)}：${failure.reason}`).join('\n')
          }
        : null
  return (
    <button
      type="button"
      data-record-id={node.recordId}
      className={`df-node${selected ? ' is-selected' : ''}${moving ? ' is-moving' : ''}${connecting ? ' is-connecting' : ''}${armed ? ' is-connectable' : ''}${draggable && !armed && !connecting ? ' is-draggable' : ''}${dragging ? ' is-dragging' : ''}${activeJobs.length > 0 ? ' is-generating' : ''}`}
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
      title={`${meta.label} · ${status.label}${live ? ` · ${live.label}` : ''}${lock ? ` · ${describeLease(lock)} 正在编辑` : ''}${armed ? ' · 点它连到这里' : draggable ? ' · 按住可拖动' : ''}`}
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
            // 第 4 个参数与检查器的详情一致（referenceDetail 也传 true）：资产库没这一条时，
            // 用场景投影里那份 name / variantLabel / versions 兜底。两边各用一套的话，
            // 同一条引用会在检查器里解析得好好的、在卡片上却显示「实体缺失」。
            const resolved = resolveReference(reference, assets, assetsReady, true)
            return (
              <span
                key={`${reference.entityId}-${index}`}
                className={`df-ref-pill${resolved.error ? ' is-missing' : ''}${onOpenReference ? ' is-openable' : ''}`}
                title={`${resolved.error ?? resolved.mode}${onOpenReference ? ' · 点开这条引用的详情' : ''}`}
                // 引用徽标可点开详情：先拦住指针事件，免得这一下被卡片当成「拖节点」或「连线」起手；
                // 点它**不改**任何引用配置，也不进什么引用画布（只把检查器的那条详情展开）。
                role={onOpenReference ? 'button' : undefined}
                onPointerDown={(event) => { if (onOpenReference) event.stopPropagation() }}
                onClick={(event) => {
                  if (!onOpenReference) return
                  event.stopPropagation()
                  onOpenReference(node.recordId, reference)
                }}
              >
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
      {/* 这个节点**自己**出的图 / 视频。与上面那排引用徽标分开摆、中间加一条分隔线：
          上面那排说的是「这个节点引用了谁」，这里说的是「这个节点自己出了什么」——
          混成一块就会把引用的设定图看成这个节点出的图（桌面端同样是分两块画的）。 */}
      {ownMedia.length > 0 && (
        <>
          <span className="df-node-divider" aria-hidden="true" />
          <span className="df-node-media">
            {media.images.map((image) => brokenImages.includes(image.id)
              // 图取不到时**留一个同样大小的空位**，不是把 img 收起来：收起来卡片会矮一截，
              // 一圈卡片跟着跳（桌面端这里写的是「无图」两个字）。占位仍然可点：大图那边可能读得出来。
              ? <span
                key={image.id}
                className="df-node-media-tile is-empty"
                title={`${image.name} · 图片无法读取（点它到检查器里重试）`}
                onPointerDown={(event) => { if (onOpenMedia) event.stopPropagation() }}
                onClick={(event) => openOwnImage(event, image.id)}
              >无图</span>
              : <img
                key={image.id}
                className="df-node-media-tile"
                src={attachmentFileUrl(node.recordId, image.id)}
                alt={image.name}
                loading="lazy"
                title={`${image.name}${image.source ? ` · ${image.source}` : ''} · 点它看大图（检查器里可下载）`}
                onPointerDown={(event) => { if (onOpenMedia) event.stopPropagation() }}
                onClick={(event) => openOwnImage(event, image.id)}
                onError={() => setBrokenImages((current) => current.includes(image.id) ? current : [...current, image.id])}
              />)}
            {/* 视频在卡片上只放一枚明确的标识：既不抓首帧（要在每张卡上跑一次解码），也不塞播放器
                （桌面端这里画的也是「▶」）。真正播放去检查器的产物附件，那里用的是同一份字节。 */}
            {media.videos.map((video) => (
              <span key={video.id} className="df-node-media-tile is-video" title={`${video.name} · 在检查器的产物附件里播放`}>▶</span>
            ))}
          </span>
        </>
      )}
      <span className="df-node-foot">
        <span className="df-node-status" style={{ color: live?.hex ?? status.hex }} title={live?.title ?? `节点状态：${status.label}`}>
          {live?.label ?? status.label}
        </span>
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

/** 候选卡上的按下不能落到画布手势上（拖节点 / 从圆点连线 / 空白处平移）。 */
function stopCanvasGesture(event: React.PointerEvent) {
  event.stopPropagation()
}

/**
 * 一张节点出图的候选卡。
 *
 * 临时的：它不是画布节点，不参与连线，也不在正式数据里（见 candidateBatch.ts）。
 * 状态与结果都来自任务（复用现有轮询与 SSE，不另开一套）；耗时由卡片自己按秒走——
 * 谁显示谁计时，整张画布不跟着每秒重画。
 */
function CandidateCard({ slot, position, index, startedAt, nodeId, faceDown = false, showQuality = false, onAdopt, onOpen }: {
  slot: CandidateSlotView
  position: { left: number; top: number }
  index: number
  startedAt: number
  nodeId: string
  /** 开奖模式：成功的那几张也不露图，只显示卡背与「待开奖」。 */
  faceDown?: boolean
  /** 判档开关：关着时小牌写「未判档」，也不显示服务端可能已有的档位。 */
  showQuality?: boolean
  onAdopt?: (nodeId: string, jobId: string, outputIndex: number) => void
  onOpen?: (jobId: string, outputIndex: number, name: string) => void
}) {
  const [, tick] = useState(0)
  const busy = slot.status === 'waiting' || slot.status === 'running'
  useEffect(() => {
    if (!busy) return
    const timer = window.setInterval(() => tick((value) => value + 1), 1000)
    return () => window.clearInterval(timer)
  }, [busy])
  const seconds = Math.max(0, Math.round((Date.now() - startedAt) / 1000))
  const name = `第 ${index + 1} 张 · ${slot.label}`
  // 失败与取消不盖卡背：它们没有可揭晓的图，必须让人一眼看到真实状态。
  const covered = faceDown && slot.status !== 'failed' && slot.status !== 'cancelled'
  // 卡背盖着时不露档位：那会提前泄露开奖结果。
  const quality = showQuality && !covered ? slot.quality : null
  const badge = qualityBadge(quality)
  const cracked = qualityIsCracked(quality)
  const qualityTitle = quality?.reason?.trim()
    || (quality?.negativeHits?.length ? `命中负面词：${quality.negativeHits.join('、')}` : '')
  return (
    <div
      className={`df-candidate-card is-${slot.status}${covered ? ' is-facedown' : ''}${cracked ? ' is-cracked' : ''}`}
      style={{ left: position.left, top: position.top, width: CANDIDATE_CARD.width, height: CANDIDATE_CARD.height }}
      onPointerDown={stopCanvasGesture}
    >
      {covered && (
        <span className="df-candidate-back" aria-hidden="true">
          <span className="df-candidate-back-line" />
          <span className="df-candidate-back-mark"><span>◈</span></span>
        </span>
      )}
      <span className="df-candidate-index" aria-hidden="true">{index + 1}</span>
      {slot.status === 'succeeded' && slot.imageUrl && !covered
        ? (
          <img
            className="df-candidate-thumb"
            src={slot.imageUrl}
            alt={name}
            loading="lazy"
            title="点它看大图"
            onPointerDown={stopCanvasGesture}
            onClick={(event) => { event.stopPropagation(); onOpen?.(slot.jobId, slot.outputIndex, name) }}
          />
        )
        : (
          <span className="df-candidate-state">
            {covered && slot.status === 'succeeded' ? '待开奖' : slot.label}
            {/* 有可靠百分比就显示；没有就只说「生成中」+ 耗时，不编一个数字出来。 */}
            {slot.progress !== null && <span className="df-candidate-percent df-mono">{Math.round(slot.progress)}%</span>}
            {busy && <span className="df-dim df-mono">{seconds}s</span>}
          </span>
        )}
      {slot.status === 'succeeded' && !covered && (
        <span className="df-candidate-quality" style={{ color: badge.color }} title={qualityTitle || badge.text}>
          {badge.text}
        </span>
      )}
      {slot.failure.length > 0 && <span className="df-candidate-fail" title={slot.failure}>{slot.failure}</span>}
      {slot.adopted && <span className="df-candidate-note">已采用</span>}
      {covered && slot.status === 'succeeded' && !slot.adopted && (
        <span className="df-candidate-note">开奖后可挑</span>
      )}
      {slot.adoptable && !faceDown && (
        <button
          type="button"
          className="df-candidate-action"
          title="采用这一张：追加到原节点"
          onPointerDown={stopCanvasGesture}
          onClick={(event) => { event.stopPropagation(); onAdopt?.(nodeId, slot.jobId, slot.outputIndex) }}
        >用这一张</button>
      )}
    </div>
  )
}
