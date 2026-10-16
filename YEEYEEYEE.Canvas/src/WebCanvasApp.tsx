import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { errorMessage, request } from './api'
import { mapAssets, recordReferences, resolveReference, type Asset } from './assets'
import { ALL_CHAPTERS_ID, chapterIdOf } from './ChapterView'
import { CANVAS_VERSION } from './Protocol/VersionedMessages'
import { SessionPanel } from './SessionPanel'
import { canEdit, gateOf, parseAuthState, roleLabel, type AuthState } from './SessionView'
import { AgentPanel, type NodeAgentRequest, type WebJob, type WebSkill } from './shell/AgentPanel'
import { candidateBatchViews, type CandidateBatch } from './shell/candidateBatch'
import { jobOutputUrl } from './shell/assetImages'
import type { AgentCanvasPreview } from './shell/agentPreview'
import { DEFAULT_VIDEO_PARAMS, buildInvocationBody, videoBackendOf, videoEffectiveSeconds, videoSeconds, videoSize, videoSizeOptions, type VideoBackend, type VideoParams } from './shell/videoParams'
import { ChapterTree } from './shell/ChapterTree'
import { InspectorPanel } from './shell/InspectorPanel'
import { EntityManager } from './shell/EntityManager'
import { parseLayoutPlan, type LayoutPlan, type LayoutScope } from './shell/layoutPlan'
import { describeLease, leaseCovering, shouldHoldNodeLease, clientLabel } from './shell/locks'
import { buildNodeMenu, parseNodeAssist, PRODUCT_VIDEO_SUGGESTION_ID, type NodeAssistPlan, type NodeMenuItem } from './shell/nodeMenu'
import { RightDock } from './shell/RightDock'
import { SettingsPanel } from './shell/SettingsPanel'
import {
  canvasBounds, chapterGroups, isEditableRecord, kindOf, NODE_KINDS, nodeX, nodeY, parseScene, recordContent,
  recordTitle, PLANNING_GROUP_ID, UNFILED_GROUP_ID, type ShellScene, type ViewRecord
} from './shell/records'
import { canApplyScene, canvasChangeText, followAction, hasUnsavedDraft, isOtherRevision, type PresencePerson } from './shell/serverEvents'
import { useCanvasJobs } from './shell/useCanvasJobs'
import { referenceKey } from './shell/referenceDetail'
import { useLeases } from './shell/useLeases'
import { useNodeLease } from './shell/useNodeLease'
import { usePresence } from './shell/usePresence'
import { useServerEvents } from './shell/useServerEvents'
import { uiText, uiTextFill } from './shell/uiText'
import { Workspace } from './shell/Workspace'
import {
  WorkbenchShell, type DockMode, type RailSection, type StatusFacts, type WorkbenchChrome, type WorkbenchView
} from './shell/WorkbenchShell'
import './session.css'
// 令牌放最后：它定义的 :root 与 body 是整页的底色与字体，得压过前面那份样式。
import './shell/tokens.css'

/**
 * 网页端工作台的编排层。
 *
 * 这里只做三件事：取数据、管状态、把插槽交给外壳。界面怎么摆全在 shell/ 下，
 * 所以这个文件里没有一处布局，只有「谁在什么时候调用哪个接口」。
 *
 * 与旧版 WebCanvasApp 的差别值得记一笔：旧版把整页写在一处、用 .workflow-shell 那套类，
 * 形状是一个「场景记录列表 + 侧栏」，与桌面端毫无关系。现在它是桌面工作台的镜像。
 */

// 既有用例从 WebCanvasApp 里取这三个函数，它们已经搬到 assets.ts；
// 这里保留出口，免得一次搬迁就顺带改测试。
export { mapAssets, recordAttachments, recordReferences, resolveReference } from './assets'

type Notice = { kind: 'success' | 'error' | 'info'; message: string }

export function WebCanvasApp({ onSwitchBlocked }: { onSwitchBlocked?: (blocked: boolean) => void } = {}) {
  const [auth, setAuth] = useState<AuthState | null>(null)
  const [authFailure, setAuthFailure] = useState('')
  const [scene, setScene] = useState<ShellScene | null>(null)
  const [assets, setAssets] = useState<Asset[]>([])
  const [assetsReady, setAssetsReady] = useState(false)
  const [selectedId, setSelectedId] = useState('')
  const [title, setTitle] = useState('')
  const [content, setContent] = useState('')
  const [notice, setNotice] = useState<Notice>({ kind: 'info', message: '登录后加载画布。' })
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [editHistoryState, setEditHistoryState] = useState<{ revision: number; undo: number; redo: number } | null>(null)
  const historyInFlight = useRef(false)
  const [skills, setSkills] = useState<WebSkill[]>([])
  const [taskBusy, setTaskBusy] = useState(false)
  /**
   * 节点出图的候选批次（临时卡片，见 shell/candidateBatch.ts）。
   *
   * 只活在这一次会话里——与 Desktop 的 ghost 批次同一条规则：不进正式画布数据、不进撤销，
   * 页面重载之后就没了（产物文件仍在资源目录里，不会因为这个被删）。
   */
  const [candidateBatches, setCandidateBatches] = useState<CandidateBatch[]>([])
  /** 右键菜单里选的张数：一次提交几张，就在原节点旁摆几个候选位。 */
  const [imageCount, setImageCount] = useState(1)
  /** 右键菜单里选的图片比例（只对声明了比例的技能有用，默认给它的第一个）。 */
  const [imageRatio, setImageRatio] = useState('')
  /**
   * 节点出视频的参数（时长/边长），默认取 videoParams 里那份。
   * 与图片那两行同一个道理：不给用户一个界面的参数，就等于替他默默选了一个。
   */
  const [videoParams, setVideoParams] = useState<VideoParams>(DEFAULT_VIDEO_PARAMS)
  /** 候选产物的大图浮层。画布世界带 transform，浮层得摆在画布之外。 */
  const [candidateFocus, setCandidateFocus] = useState<{ jobId: string; outputIndex: number; name: string } | null>(null)
  const [nodeAgentRequest, setNodeAgentRequest] = useState<NodeAgentRequest | null>(null)
  const [agentPreview, setAgentPreview] = useState<AgentCanvasPreview | null>(null)
  /** 画布上点了引用徽标：把「哪条引用、属于哪个节点」交给检查器去展开（只影响展示）。 */
  const [referenceFocus, setReferenceFocus] = useState<{ recordId: string; key: string; nonce: number } | undefined>(undefined)
  // 画布上点了节点自己出的一张图：把「哪个节点的哪一件产物」交给检查器去开大图
  // （浮层摆在检查器里，画布世界带 transform，fixed 在里面会变成相对世界定位）。
  const [mediaFocus, setMediaFocus] = useState<{ recordId: string; attachmentId: string; nonce: number } | undefined>(undefined)
  const [view, setView] = useState<WorkbenchView>('canvas')
  const [section, setSection] = useState<RailSection>('story')
  const [search, setSearch] = useState('')
  const [dockOpen, setDockOpen] = useState(true)
  const [dockMode, setDockMode] = useState<DockMode>('inspector')
  const [activeChapter, setActiveChapter] = useState<string>(ALL_CHAPTERS_ID)
  const [lastSaved, setLastSaved] = useState('')
  const [layoutPlan, setLayoutPlan] = useState<LayoutPlan | null>(null)
  const [layoutScope, setLayoutScope] = useState<LayoutScope>('all')
  const [layoutBusy, setLayoutBusy] = useState(false)
  /** 检查器的编辑框是否拿到焦点——它是「打算改」的信号，用来决定要不要去占锁。 */
  const [editorFocused, setEditorFocused] = useState(false)
  /**
   * 连接模式：从**当前选中的节点**拉一根线出去。
   * 起点不单独存一份——存两份的话，「选中被清掉、起点还留着」这种半截状态就得靠额外的清理代码兜住。
   */
  const [connecting, setConnecting] = useState(false)
  /**
   * 节点右键菜单。`plan` 是服务端算出来的那份协助计划（与桌面端右键同一份来源），
   * 菜单项在渲染时由 `buildNodeMenu` 拼——「有哪些建议」只有服务端一份，这里不留副本。
   */
  const [nodeMenu, setNodeMenu] = useState<{ recordId: string; x: number; y: number; plan: NodeAssistPlan } | null>(null)
  /** 服务端推来一条「画布变了」，且那个修订和我手上的不是同一个——我这份已经不是最新的了。 */
  const [remoteChange, setRemoteChange] = useState<{ revision: number; text: string } | null>(null)
  const generation = useRef(0)
  // 编辑锁读取：正常情况下靠推送立刻刷新，15 秒的轮询是兜底（推送断了、或锁因超时自然消失）。
  const { leases, error: leaseError, invalidCount: leaseInvalidCount, refresh: refreshLeases } = useLeases(!!auth?.user)
  // 在线名单：与锁是两条独立的路（在线≠拥有锁）。推送到了立刻刷新，15 秒轮询兜底。
  const { people: onlinePeople, error: presenceError, refresh: refreshPresence } = usePresence(!!auth?.user)

  const records = useMemo(() => scene?.records ?? [], [scene])
  const edges = useMemo(() => scene?.edges ?? [], [scene])
  const groups = useMemo(() => chapterGroups(records), [records])
  const selected = useMemo(() => records.find((item) => item.recordId === selectedId) ?? null, [records, selectedId])
  const dirty = !!selected && (title !== recordTitle(selected) || content !== recordContent(selected))
  /**
   * 后台刷新（followScene）的代号：**独立于 generation**。
   * 两边各认各的——混用一个计数器的话，整份重载的半途状态会被一次后台刷新顶掉。
   */
  const followRun = useRef(0)
  /**
   * 「此刻手上有没有没提交的编辑」（聚焦中或内容已改）。
   * 异步回包回来时要复查一眼：请求发出那一刻干净，不代表回来的那一刻也干净——
   * 用户完全可能在往返期间点进编辑框开始敲。
   */
  const drafting = useRef(false)
  drafting.current = dirty || editorFocused
  useEffect(() => {
    onSwitchBlocked?.(dirty || saving || loading || layoutBusy || taskBusy)
  }, [dirty, saving, loading, layoutBusy, taskBusy, onSwitchBlocked])
  useEffect(() => {
    if (!dirty) return
    const prevent = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = '' }
    window.addEventListener('beforeunload', prevent)
    return () => window.removeEventListener('beforeunload', prevent)
  }, [dirty])
  const readOnly = scene?.readOnly === true
  const role = auth?.user?.role ?? 'Viewer'
  const editable = canEdit(role) && !readOnly
  const myUserId = auth?.user?.id ?? ''
  // 选中节点上的锁：别人持有的（含整棵树锁）会让写入与检查器都变成只读。
  const selectedLease = selected ? leaseCovering(leases, selected.recordId) : null
  const polledBlocked = selectedLease && selectedLease.userId !== myUserId ? selectedLease : null

  // 网页端自己持锁：焦点进了编辑框、或草稿还没保存，才去占（见 shouldHoldNodeLease）。
  // 光选中不算——点着看一圈就撒一地锁，等于把别人挡在外面而自己什么也没改。
  const holdLease = shouldHoldNodeLease({ editable, focused: editorFocused, dirty })
  const nodeLeaseState = useNodeLease({
    recordId: selected?.recordId ?? '',
    wanted: holdLease,
    // 拿到/还回锁之后立刻刷新一次锁列表：否则自己那张卡的徽标要等下一次轮询才出现。
    onChanged: () => void refreshLeases()
  })
  // 申请被拒时拿到的持有者是**刚刚发生**的冲突，比 15 秒轮询那份新；两边取其一即可。
  const blockedBy = nodeLeaseState.blockedBy ?? polledBlocked
  const heldByMe = nodeLeaseState.held
  // 连接模式的起点就是选中的那个节点。选中被清掉（点了空白处）时它自然变成 null，模式随之作废——
  // 不会留下「没有起点的连接模式」这种点了没反应的状态。
  const connectFrom = connecting && selected ? selected.recordId : null

  /**
   * 生成任务（`GET /api/web/jobs`）：画布上的「生成中 / 失败」由它来。
   *
   * 任务归谁看它自己的 `targetNodeId`（发起时固定），**与当前选中项无关**——
   * 所以这份数据不进选中相关的那几条 effect，切换节点也不会把任务状态带到别的节点上。
   */
  const { jobs, error: jobsError, refresh: refreshJobs } = useCanvasJobs(!!auth?.user, () => {
    // 任务落终态：产物回挂是服务端在任务成功之后另做的一步（挂上时会推一条 canvas.changed）。
    // 手上干净就把画布重新取一次——推送断开时这是唯一的发现路径；有草稿就不动它。
    if (dirty || editorFocused || loading || saving) return
    void followScene()
  })

  /**
   * 候选批次此刻该显示什么。
   *
   * 状态来自 `jobs`（复用同一条轮询与 SSE，不另开一套）；「采用过了没有」看**节点自己的附件**——
   * 采用成功之后服务端会把那件产物写进节点，于是卡片自动变成「已采用」，本地不另记一份。
   */
  const candidateViews = useMemo(
    () => candidateBatchViews(candidateBatches, jobs, records),
    [candidateBatches, jobs, records]
  )

  // 变更推送：锁变了立刻刷新锁列表；画布被改了就把「已同步」改成「有新版」。
  // 推送只说「变了」，所以这里不自动重载画布——把整页视角与选中项一起抽走，比晚看到几秒更烦人。
  const events = useServerEvents({
    enabled: !!auth?.user,
    onEditsChanged: () => void refreshLeases(),
    // 有人上线/下线：立刻重取名单。载荷只说「谁变了」，名单形状只有一处（GET /api/web/presence）。
    onPresenceChanged: () => void refreshPresence(),
    onCanvasChanged: (event) => {
      // 产物回挂这条推送：任务本身的进度/成败也要跟着更新一次（任务列表与画布是两份数据）。
      if (event.scope === 'job-output') void refreshJobs()
      if (!isOtherRevision(scene?.revision, event)) return

      // 手上没有没提交的东西就跟上，省掉一次点击；有草稿就只提示。
      // 判据与桌面端（RemoteCanvasChange.Decide）是同一条：自动跟上是**替换手上这份**。
      const draft = hasUnsavedDraft(
        editorFocused,
        { title, content },
        selected ? { title: recordTitle(selected), content: recordContent(selected) } : null
      )
      if (followAction(true, draft) === 'reload') {
        setNotice({ kind: 'info', message: '别人改了画布，已自动跟进最新版本。' })
        setRemoteChange(null)
        // 整理预览的基准修订同样失效：自动跟上之后它也不能再被应用了。
        setLayoutPlan(null)
        void loadScene()
        return
      }

      const changed = event.recordId ? records.find((item) => item.recordId === event.recordId) : null
      setRemoteChange({
        revision: event.revision,
        text: canvasChangeText(event, changed ? recordTitle(changed) : undefined)
      })
      // 整理预览的基准修订随之失效：收掉它，别让人点了「应用整理」才发现。
      setLayoutPlan(null)
    }
  })

  /**
   * 选中 / 取消选中。传 null 是「在画布上点了空白处」——桌面端也是按空白先取消选中再进入平移。
   * 切换与取消都要过一遍未保存确认：把编辑框里的草稿丢掉是同样的一件事。
   */
  function selectRecord(id: string | null) {
    const item = id === null ? null : records.find((record) => record.recordId === id) ?? null
    if (id !== null && item === null) return
    if (dirty && !window.confirm('当前编辑尚未保存，确定放弃修改吗？')) return
    // 选中变了，连接模式就作废：它的起点就是选中项，留着一个换了起点的半截状态只会让人点错。
    setConnecting(false)
    setSelectedId(item?.recordId ?? '')
    setTitle(item ? recordTitle(item) : '')
    setContent(item ? recordContent(item) : '')
  }

  /**
   * 重新加载整张画布。
   *
   * `select` 是「加载完之后把我放回哪个节点上」：结构级写入（新建节点 / 新建连线 / 断开连线）之后，
   * 手上的选中项会随重载一起清掉，而按下那一下的人通常正停在某个节点上——不还回去，
   * 每做一次结构改动就得重新点一遍。写回选中项用**刚解析出来的那份记录**，
   * 不是闭包里那份旧的（旧的既没有新节点，也会把刚被改过的内容还原成旧值）。
   */
  async function loadScene(options?: { select?: string }) {
    if (dirty && !window.confirm('重新加载将放弃未保存的修改，确定继续吗？')) return
    // 整份重载之后，路上那次后台刷新（followScene）取回来的就是旧场景了：作废它。
    followRun.current++
    const run = ++generation.current
    setLoading(true)
    setNotice({ kind: 'info', message: '正在加载画布…' })
    setScene(null)
    setAssets([])
    setAssetsReady(false)
    setSkills([])
    setSelectedId('')
    setActiveChapter(ALL_CHAPTERS_ID)
    // 重新加载之后旧预览的基准修订已经不对了，留着它只会让人以为还能应用。
    setLayoutPlan(null)
    const [sceneResult, assetResult] = await Promise.allSettled([
      request<unknown>('/api/web/scene'),
      request<unknown>('/api/web/assets')
    ])
    if (run !== generation.current) return
    setLoading(false)
    try {
      if (sceneResult.status === 'rejected') throw sceneResult.reason
      const parsed = parseScene(sceneResult.value)
      setScene(parsed)
      // 重新加载之后「画布有变动」这件事就已经解决了，标记必须跟着清掉。
      setRemoteChange(null)
      // 要把选中项还回去时，用刚解析出来的这份记录——见 loadScene 的说明。
      const restored = options?.select
        ? parsed.records.find((record) => record.recordId === options.select) ?? null
        : null
      if (restored) {
        setSelectedId(restored.recordId)
        setTitle(recordTitle(restored))
        setContent(recordContent(restored))
      }
      setNotice({
        kind: 'success',
        // 章节数按**种类**数，不拿「非可编辑记录」当代理：章节可以是工作树行（recordId 是 wt-，
        // 不算可编辑记录），也可以是真实的章节节点（是 UUID，算可编辑记录）。
        // 拿后者反推前者，会在「章节已经是节点」的画布上数出 0，而左边的树里明明有好几章。
        message: `画布加载成功：修订 ${parsed.revision}，${parsed.records.filter(isEditableRecord).length} 个节点、${parsed.records.filter((item) => kindOf(item.recordType) === 'chapter').length} 个章节。${parsed.readOnly ? ' 服务端把它标成了只读。' : ''}`
      })
    } catch (error) {
      setNotice({ kind: 'error', message: `画布加载失败：${errorMessage(error)}` })
    }
    try {
      if (assetResult.status === 'rejected') throw assetResult.reason
      const { entities, invalidCount } = mapAssets(assetResult.value)
      setAssets(entities)
      setAssetsReady(true)
      if (invalidCount > 0) setNotice({ kind: 'info', message: `资产跳过 ${invalidCount} 项缺少有效 id/name 的实体。` })
    } catch (error) {
      setNotice({ kind: 'error', message: `资产加载失败：${errorMessage(error)}` })
    }
    try {
      const catalog = await request<{ skills: WebSkill[] }>('/api/web/skills')
      if (run !== generation.current) return
      if (!Array.isArray(catalog.skills)) throw new Error('响应格式不正确')
      setSkills(catalog.skills)
    } catch (error) {
      if (run === generation.current) setNotice({ kind: 'error', message: `技能加载失败：${errorMessage(error)}` })
    }
  }

  /**
   * 只把画布内容重新取一次：**不动**选中、章节筛选、草稿与提示条。
   *
   * 与 `loadScene` 的差别正是「不动视角与选中」。它用在**生成产物回挂**这种后台事件上：
   * 用户多半正盯着那个节点等结果，这时把整页重置一遍（选中清空、章节筛选回到全部）
   * 比晚几秒看到图更烦人。有草稿时什么都不做——写入权在用户手上。
   */
  async function followScene() {
    if (dirty) return
    const run = ++followRun.current
    try {
      const next = parseScene(await request('/api/web/scene'))
      // 迟到的回包不能落到界面上：期间可能已经整份重载过（那就是回退），
      // 或者用户已经开始编辑（那就是把他刚敲的东西顶掉）。见 canApplyScene。
      if (!canApplyScene({ stale: run !== followRun.current, editing: drafting.current })) return
      setScene(next)
      setRemoteChange(null)
    } catch (error) {
      if (run !== followRun.current) return
      setNotice({ kind: 'error', message: `刷新画布失败：${errorMessage(error)}` })
    }
  }

  /** 问一次服务端「我是谁」。没登录就什么都不加载——**别在没身份的时候去打画布接口**。 */
  async function refreshSession() {
    try {
      const state = parseAuthState(await request<unknown>('/api/auth/state'))
      setAuth(state)
      setAuthFailure('')
      if (state.user) void loadScene()
      else {
        setScene(null)
        setAssets([])
        setAssetsReady(false)
        setSkills([])
      }
    } catch (error) {
      setAuthFailure(`无法确认登录状态：${errorMessage(error)}`)
    }
  }

  useEffect(() => {
    void refreshSession()
    // 只跑一次；之后由登录 / 退出按钮显式触发。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // 历史数量只对相同修订有效；外部修改、登录切换和本地提交均重新读取。
  useEffect(() => {
    setEditHistoryState(null)
    if (!auth?.user || !scene || !editable) return
    let active = true
    void request<{ revision: number; undo: number; redo: number }>('/api/web/canvas/history')
      .then(value => {
        if (active && value.revision === scene.revision && Number.isInteger(value.undo) && Number.isInteger(value.redo))
          setEditHistoryState(value)
      }).catch(() => { /* 不支持普通历史的独立场景保持按钮禁用。 */ })
    return () => { active = false }
  }, [auth?.user?.id, scene?.revision, editable, saving])

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target
      if (event.defaultPrevented || event.isComposing || event.altKey || !(event.ctrlKey || event.metaKey) ||
          (target instanceof HTMLElement && (target.isContentEditable || target.closest('input, textarea, select, [role="textbox"]')))) return
      const key = event.key.toLowerCase()
      const operation = key === 'z' ? (event.shiftKey ? 'redo' : 'undo') : key === 'y' && !event.shiftKey ? 'redo' : null
      if (!operation || !scene || !editable || saving || loading || layoutBusy || dirty ||
          !editHistoryState || editHistoryState.revision !== scene.revision || editHistoryState[operation] === 0 ||
          leases.some(lease => lease.userId !== myUserId)) return
      event.preventDefault()
      void editHistory(operation)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [scene, editable, saving, loading, layoutBusy, dirty, editHistoryState, leases, myUserId, selectedId])

  // Esc 退出连接模式。只退模式、**不清选中**：清选中会牵出「未保存的修改要不要放弃」那一问，
  // 而按 Esc 的人想停的是「连线」这件事，不是想丢掉手上的草稿。
  useEffect(() => {
    if (!connecting) return
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') setConnecting(false) }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [connecting])

  // 候选大图浮层的 Escape：与引用详情、检查器大图同一个手势（浮层都摆在画布之外）。
  useEffect(() => {
    if (!candidateFocus) return
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') setCandidateFocus(null) }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [candidateFocus])

  async function save() {
    if (!selected || !scene || saving) return
    // 别人正拿着这个节点的锁：服务端也会拒，但界面不该先给一个点了必然失败的按钮。
    if (blockedBy) {
      setNotice({ kind: 'error', message: `${describeLease(blockedBy)} 正在编辑这个节点，等他保存或让管理员接管。` })
      return
    }
    if (readOnly) {
      setNotice({ kind: 'error', message: '服务端把这张画布标成了只读，拒绝保存。' })
      return
    }
    // 只读角色在服务端本来就会被拒（拿到的是 403），但界面不该先给一个点了必然失败的按钮。
    if (!canEdit(role)) {
      setNotice({ kind: 'error', message: '你的账号是只读，改不了画布。' })
      return
    }
    const id = selected.recordId
    const baseRevision = scene.revision
    const draftTitle = title
    const draftContent = content
    setSaving(true)
    setNotice({ kind: 'info', message: '正在保存…' })
    try {
      const result = await request<{ revision: number; record: ShellScene['records'][number] }>(`/api/web/records/${encodeURIComponent(id)}`, {
        method: 'PUT', body: JSON.stringify({ baseRevision, title: draftTitle, content: draftContent })
      })
      if (!Number.isFinite(result?.revision) || !result.record || result.record.recordId !== id) throw new Error('保存响应格式不正确')
      setScene((current) => current
        ? { ...current, revision: result.revision, records: current.records.map((item) => item.recordId === id ? result.record : item) }
        : current)
      setTitle(recordTitle(result.record))
      setContent(recordContent(result.record))
      setLastSaved(new Date().toLocaleTimeString('zh-CN', { hour12: false }))
      // 保存会推进修订，旧预览的基准随之失效——同上，不能留着。
      setLayoutPlan(null)
      setNotice({ kind: 'success', message: `已保存 ${id.slice(0, 8)}，画布修订 ${result.revision}。` })
    } catch (error) {
      setNotice({ kind: 'error', message: `保存失败：${errorMessage(error)}。修改仍在编辑框里；修订冲突时请重新加载画布。` })
    } finally {
      setSaving(false)
    }
  }

  /**
   * 整理布局的预览。**只算不写**：服务端按桌面端那份泳道引擎算，回泳道划分与逐节点改动，
   * 网页端把「会搬到哪里」画成虚影。写入是另一次请求，因为写到一半的坐标比没写更糟。
   */
  async function planLayout(scope: LayoutScope) {
    if (layoutBusy) return
    setLayoutBusy(true)
    setLayoutScope(scope)
    try {
      const plan = parseLayoutPlan(await request<unknown>('/api/web/layout/plan', {
        method: 'POST', body: JSON.stringify({ scope, overrideManual: false })
      }))
      setLayoutPlan(plan)
      // 虚影画在画布上，所以预览一出来就切到画布视图。
      setView('canvas')
      setNotice(plan.blocking
        ? { kind: 'error', message: '布局被阻断，画布不会改动。' }
        : { kind: 'info', message: plan.changed ? `整理预览：${plan.moves.length} 个节点会移动，确认后才写入。` : '位置已经符合泳道布局，无需改动。' })
    } catch (error) {
      setLayoutPlan(null)
      setNotice({ kind: 'error', message: `整理预览失败：${errorMessage(error)}` })
    } finally {
      setLayoutBusy(false)
    }
  }

  /** 应用整理。计划由服务端**重算**，这里只报「哪个范围」与「要不要连手动摆放的一起动」。 */
  async function applyLayout(overrideManual: boolean) {
    if (!layoutPlan || !scene || layoutBusy) return
    setLayoutBusy(true)
    try {
      const result = await request<{ revision: number; moved: number; records: ShellScene['records'] }>('/api/web/layout/apply', {
        method: 'POST',
        body: JSON.stringify({ baseRevision: scene.revision, scope: layoutScope, overrideManual, client: 'web' })
      })
      if (!Number.isFinite(result?.revision) || !Array.isArray(result.records)) throw new Error('整理响应格式不正确')
      setScene((current) => current
        ? { ...current, revision: result.revision, records: current.records.map((item) => result.records.find((updated) => updated.recordId === item.recordId) ?? item) }
        : current)
      setLayoutPlan(null)
      setLastSaved(new Date().toLocaleTimeString('zh-CN', { hour12: false }))
      setNotice({
        kind: 'success',
        message: result.moved > 0
          ? `已整理：移动了 ${result.moved} 个节点，画布修订 ${result.revision}。`
          : '位置已经排好了，画布没有改动。'
      })
      // 写入会让服务端拿到整棵树锁（自己持有就是续期），所以顺手刷新一次锁列表。
      void refreshLeases()
    } catch (error) {
      const message = errorMessage(error)
      setNotice({ kind: 'error', message: `整理失败：${message}` })
      // 修订冲突说明画布在这中间被改过：这份预览的基准已经失效，收掉它让用户重新加载。
      if (message.includes('SCENE_REVISION_CONFLICT')) setLayoutPlan(null)
      // 别人正拿着整棵树锁：刷新锁列表，把「谁在编辑」显示出来。
      if (message.includes('EDIT_CONFLICT')) void refreshLeases()
    } finally {
      setLayoutBusy(false)
    }
  }

  async function logout() {
    try {
      await request('/api/auth/logout', { method: 'POST' })
    } catch {
      // 退出失败也要重新问一次身份：cookie 可能已经失效，界面必须跟着真实状态走。
    }
    await refreshSession()
  }

  // 还没问出「我是谁」之前不要画工作台：先显示登录页再跳走会闪一下，也容易让人以为要填两次。
  if (!auth) return <div className="df-gate">
    <div className="df-gate-card">
      <h2 className="df-gate-title">正在确认登录状态…</h2>
      <p className="df-notice">如果一直停在这里，说明服务端没起来。</p>
    </div>
  </div>

  if (authFailure) return <div className="df-gate">
    <div className="df-gate-card">
      <h2 className="df-gate-title">无法确认登录状态</h2>
      <p className="df-notice is-error">{authFailure}</p>
      <button type="button" className="df-mini-button" style={{ alignSelf: 'flex-start' }} onClick={() => void refreshSession()}>重试</button>
    </div>
  </div>

  if (gateOf(auth) !== 'ready') return <SessionPanel state={auth} onChanged={() => void refreshSession()} />

  const user = auth.user!
  const bounds = canvasBounds(records)
  const nodeCount = records.filter(isEditableRecord).length

  const sync: { label: string; tone: StatusFacts['syncTone'] } = notice.kind === 'error'
    ? { label: '有错误', tone: 'error' }
    : saving ? { label: '保存中', tone: 'busy' }
      : loading ? { label: '加载中', tone: 'busy' }
        : readOnly ? { label: '只读画布', tone: 'idle' }
          : dirty ? { label: '有未保存修改', tone: 'busy' }
            // 别人改了画布：说清楚是谁改的、改了什么，并给一个动作（下面那个按钮）。
            // 文案不写「有新版」：修订是哈希，谁更新排不出来，只能说「有变动」，动作也只能是重新加载。
            : remoteChange ? { label: `画布有变动：${remoteChange.text}`, tone: 'stale' }
              : { label: '已同步', tone: 'ok' }

  const status: StatusFacts = {
    text: notice.message,
    revision: `修订 ${scene?.revision ?? '—'}`,
    syncLabel: sync.label,
    syncTone: sync.tone,
    syncAction: remoteChange ? { label: '重新加载', onClick: () => void loadScene() } : undefined,
    nodes: `${nodeCount} 节点`,
    // 连线是服务端投影过来的真数（只投影两端都还在的那些），所以这里可以照实报数。
    edges: `${edges.length} 连线`,
    canvasSize: `画布 ${bounds.width}×${bounds.height}`,
    version: scene?.formatVersion != null ? `格式 v${scene.formatVersion}` : '格式 —',
    saved: `最后保存 ${lastSaved || '—'}`
  }

  const chrome: WorkbenchChrome = {
    version: `v${CANVAS_VERSION}`,
    projectName: scene?.projectName ?? '服务端项目',
    projectSubtitle: scene
      ? `${readOnly ? '只读' : '可编辑'} · 修订 ${scene.revision} · ${roleLabel(user.role)}`
      : '尚未加载',
    canvasTitle: scene?.canvasTitle ?? '未命名画布',
    view,
    onView: setView,
    section,
    onSection: (next) => {
      setSection(next)
      if (next === 'story') setView('canvas')
    },
    search,
    onSearch: setSearch,
    dockOpen,
    onToggleDock: () => setDockOpen((current) => !current),
    agentActive: dockOpen && dockMode === 'agent',
    onOpenAgent: () => { setDockOpen(true); setDockMode('agent') },
    onOpenSettings: () => { setDockOpen(true); setDockMode('settings') },
    onSave: () => void save(),
    saveLabel: saving ? '保存中…' : '保存修订',
    saveDisabled: !editable || !dirty || saving || !!blockedBy
  }

  const inspectorHint = !selected
    ? '在画布上点一个节点后可以改它的名称与内容'
    : blockedBy ? `${describeLease(blockedBy)} 正在编辑这个节点，等他保存或让管理员接管`
      : heldByMe ? '锁在你手上：离开编辑框后会自动还回去'
        : readOnly ? '这张画布被服务端标成只读，改不了'
          : !canEdit(role) ? '你的账号是只读，改不了画布'
            : dirty ? uiText('inspector.applyHint') : '点进编辑框会先占锁，别人这时改不了这个节点'

  // 通道自己的问题。它不该把画布变成不可用，但也不能悄悄吞掉——
  // 否则界面上的「没有人编辑」会被读成「现在没人编辑」，「已同步」会被读成「推送通着」。
  const channelNotice = [
    nodeLeaseState.error ? `编辑锁：${nodeLeaseState.error}` : '',
    leaseError ? `编辑锁暂时读不到：${leaseError}` : '',
    leaseInvalidCount > 0 ? `有 ${leaseInvalidCount} 条锁记录格式不对，已跳过` : '',
    presenceError ? `在线名单暂时读不到：${presenceError}` : '',
    events.error
  ].filter((line) => line.length > 0).join(' · ')

  /**
   * 为什么现在不能进连接模式。空串表示可以。
   * 顺序就是「先撞上哪一条」：画布只读 → 角色只读 → 没选起点 → 起点被别人锁着。
   * 连接模式下这个值不参与按钮的禁用判断（那时按钮是出口，见 Workspace）。
   */
  const connectBlocked = readOnly ? '这张画布被服务端标成只读，改不了'
    : !canEdit(role) ? '你的账号是只读，改不了画布'
      : !selected ? '先选中一个节点作为起点'
        : blockedBy ? `${describeLease(blockedBy)} 正在编辑这个节点，等他保存或让管理员接管`
          : ''

  /**
   * 结构级写入：新建一个节点。
   *
   * 与「保存标题内容」分开走：那条路改的是一个节点的文字，这条改的是**画布结构**，
   * 服务端按结构级对待（要账号、要 canvas.edit、要整棵树锁），所以它不是「保存」的一部分。
   * 位置由服务端算（同一章里最右那个的右边一列），这里只交代「建在哪一章、什么类别」。
   */
  async function createNode(chapterId: string, recordType: string) {
    if (!scene) return
    if (!canEdit(role)) {
      setNotice({ kind: 'error', message: '你的账号是只读，改不了画布。' })
      return
    }

    setSaving(true)
    setNotice({ kind: 'info', message: '正在新建节点…' })
    try {
      const created = await request<{ revision: number; nodeId: string }>('/api/web/records', {
        method: 'POST',
        body: JSON.stringify({
          baseRevision: scene.revision,
          recordType,
          chapterId: chapterId === ALL_CHAPTERS_ID ? null : chapterId
        })
      })
      if (!created?.nodeId) throw new Error('新建响应里没有节点 ID')
      // 建完停在新节点上：`loadScene({ select })` 用**刚解析出来的**记录回填选中项——
      // 以前这里是在重载之后按闭包里那份旧记录去选，新节点还不在里面，于是始终选不中。
      await loadScene({ select: created.nodeId })
      setNotice({ kind: 'info', message: '已新建节点。' })
    } catch (error) {
      setNotice({ kind: 'error', message: `新建失败：${errorMessage(error)}` })
    } finally {
      setSaving(false)
    }
  }

  /**
   * 结构级写入：删掉一个节点（连带它的连线），确认后保存到普通编辑历史。
   *
   * `targetRecordId` 只有右键菜单会给（它作用于**被右键的那一张**，不是「当前选中的那张」）：
   * 菜单里点「删除节点」时选中的确实是它，但把「删谁」说清楚比依赖那次选中可靠。
   */
  async function deleteNode(targetRecordId?: string) {
    const target = targetRecordId
      ? records.find((item) => item.recordId === targetRecordId) ?? null
      : selected
    if (!scene || !target) return
    if (!canEdit(role)) {
      setNotice({ kind: 'error', message: '你的账号是只读，改不了画布。' })
      return
    }
    if (!window.confirm(`删掉「${recordTitle(target)}」？它的连线会一并删掉。`)) return

    setSaving(true)
    setNotice({ kind: 'info', message: '正在删除…' })
    try {
      await request(`/api/web/records/${encodeURIComponent(target.recordId)}?baseRevision=${scene.revision}`, {
        method: 'DELETE'
      })
      await loadScene()
      setNotice({ kind: 'info', message: '已删除。' })
    } catch (error) {
      setNotice({ kind: 'error', message: `删除失败：${errorMessage(error)}` })
    } finally {
      setSaving(false)
    }
  }

  /**
   * 右键一张节点卡：先问服务端「这个节点能做什么」（协助计划），再摆菜单。
   *
   * 为什么要往返一次 HTTP 而不是本地拼菜单：**菜单的内容不是网页端定的**。它由共享的
   * `NodeAssistPlanner` 按节点类型与上游素材算出来，桌面端右键用的是同一份——
   * 网页端在这里抄一遍，等于把「有哪些建议」变成两份会各自演化的东西。
   *
   * 取不到就明说，不摆一个空菜单：空菜单看起来像「这个节点没什么可做的」，那是另一回事。
   */
  async function openNodeMenu(recordId: string, position: { x: number; y: number }) {
    setNodeMenu(null)
    try {
      const plan = parseNodeAssist(await request<unknown>(`/api/web/records/${encodeURIComponent(recordId)}/assist`))
      setNodeMenu({ recordId, x: position.x, y: position.y, plan })
    } catch (error) {
      setNotice({ kind: 'error', message: `读取这个节点能做些什么失败：${errorMessage(error)}` })
    }
  }

  /**
   * 菜单里的一项被选中。菜单**先关掉**：它是瞬时面板，动作失败也由 notice 说原因，
   * 留着菜单反而盖住那条原因。
   */
  async function runNodeMenuItem(item: NodeMenuItem) {
    const menu = nodeMenu
    setNodeMenu(null)
    if (!menu) return
    if (item.action === 'editNode') {
      // 「编辑节点…」在桌面端是开一个窗口；网页端没有窗口，对应物就是「切到检查器 + 光标进去」。
      setDockOpen(true)
      setDockMode('inspector')
      setEditorFocused(true)
      return
    }
    if (item.action === 'deleteNode') {
      await deleteNode(menu.recordId)
      return
    }

    const suggestion = menu.plan.suggestions.find((entry) => entry.id === item.id)
    if (!suggestion) return
    if (item.action === 'copyPrompt') {
      try {
        await navigator.clipboard.writeText(suggestion.prompt)
        setNotice({ kind: 'success', message: '提示词已复制到剪贴板。' })
      } catch {
        setNotice({ kind: 'error', message: '复制失败：浏览器没有给剪贴板权限（要用 https 或 localhost）。' })
      }
      return
    }
    if (item.action === 'runAgent') {
      const target = records.find(record => record.recordId === menu.recordId)
      if (!editable || !scene || !target || !isEditableRecord(target) || !item.enabled || !suggestion.canRun || !suggestion.prompt.trim() || nodeAgentRequest) return
      setNodeAgentRequest({ id: crypto.randomUUID(), prompt: suggestion.prompt, nodeId: target.recordId,
        chapterId: chapterIdOf(target) || (typeof target.record.workTreeItemId === 'string' ? target.record.workTreeItemId : undefined),
        baseRevision: scene.revision, title: recordTitle(target) })
      setDockOpen(true)
      setDockMode('agent')
      return
    }
    if (item.action === 'runImage') {
      const imageSkill = skills.find(skill => skill.capability === 'TextToImage')
      if (!imageSkill || taskBusy || !suggestion.prompt.trim() || !editable) {
        setNotice({ kind: 'error', message: '没有可调用的文生图技能，或已有任务正在提交。' })
        return
      }
      // 生成比例：技能自己声明了可选比例时（图片 API 就是这一类）才带这个字段，
      // 认不出就退回它声明的第一个——绝不自己编一个服务端不认的值。
      const ratios = imageSkill.aspectRatios ?? []
      const aspectRatio = ratios.length > 0 ? (ratios.includes(imageRatio) ? imageRatio : ratios[0]) : ''
      await runImageBatch(imageSkill, suggestion.prompt, menu.recordId, aspectRatio)
    }
    if (item.action === 'runVideo') {
      const videoSkill = skills.find(skill => skill.capability === 'TextToVideo')
      if (!videoSkill || taskBusy || !suggestion.prompt.trim() || !editable) {
        setNotice({ kind: 'error', message: '没有可调用的文生视频技能，或已有任务正在提交。' })
        return
      }
      await submitNodeVideo(videoSkill, suggestion.prompt, menu.recordId)
    }
  }

  /**
   * 节点出视频：**直接提交、成功就直接挂回原节点**。
   *
   * 与出图那条路的关键差别（也是与 Desktop 一致的地方，见 MainWindow.axaml.cs 的 RunVideoAsync）：
   * 一次就是一版，**没有「一批」也没有「挑一张」**——所以不进候选卡/采用那一套，产物由服务端
   * 在任务成功后按 targetNodeId 回挂到原节点，与普通回挂走同一条受保护的路。
   *
   * 目标节点在提交这一刻就固定（菜单当时那个节点），之后切选中不影响它。首帧素材不在这里挑：
   * 服务端按这个目标节点跑共享的 `GenerationAudit.CheckVideoAssets`（与桌面端同一份校验）解析出
   * 采用的首帧，缺素材/依据变化都会以 GENERATION_ASSETS_INVALID 挡回来，不会静默降级成文生视频。
   */
  async function submitNodeVideo(skill: WebSkill, prompt: string, nodeId: string) {
    if (!auth?.user || taskBusy) return
    setTaskBusy(true)
    try {
      // 请求体按**服务端声明的后端**组：接口站那条路只认秒，带帧数/种子会被自己拒收（见 videoParams）。
      const backend = videoBackendOf(skill)
      const body = {
        ...buildInvocationBody(skill.id, prompt, videoParams, crypto.randomUUID(), backend),
        targetNodeId: nodeId
      }
      const job = await request<WebJob>(`/api/web/skills/${encodeURIComponent(skill.id)}/invoke`,
        { method: 'POST', body: JSON.stringify(body) })
      // 两条路的「这条实际多长 / 多大」不是一回事：LTX 是秒折帧后略长，接口站是所求即所得、画幅由服务端定。
      const shape = backend === 'http'
        ? `${videoSeconds(videoParams.seconds)} 秒 · 画幅由服务端按模型默认档位决定`
        : `约 ${videoEffectiveSeconds(videoParams.seconds).toFixed(2)} 秒 · ${videoSize(videoParams.size)}px`
      setNotice({
        kind: 'info',
        message: `视频任务已提交（${shape}）：出好后会直接挂回这个节点。任务 ${job.jobId.slice(0, 8)}`
      })
      // 立刻取一次任务列表：新任务要等下一次轮询才出现，而按下生成的人正等着看状态。
      void refreshJobs()
    } catch (error) {
      // 素材/依据不齐、目标锁定或权限不足都在这里如实说（服务端的原话），节点上不留任何痕迹。
      setNotice({ kind: 'error', message: `出视频没能提交：${errorMessage(error)}` })
    } finally { setTaskBusy(false) }
  }

  /**
   * 节点出图：在**原节点**旁摆一排候选卡，然后按张数逐个提交。
   *
   * 顺序与 Desktop 一致（`RunImageBatchAsync` 也是先建满槽位再提交）：
   * 卡片**先**出现，用户按下生成就看见「在等几张」，而不是等任务回来才有反应；
   * 每一格与一个任务一一对应，提交失败的那一格当场说失败，不留「永远等待」的假卡片。
   *
   * 提交走的还是原来那条路（同一个技能、同一段提示词、服务端按节点引用解析参考素材），
   * 只多一个 `candidateBatch` 标记：**产物先留在任务里**，等用户在画布上采用才进节点。
   */
  async function runImageBatch(imageSkill: WebSkill, prompt: string, nodeId: string, aspectRatio: string) {
    const count = Math.min(Math.max(imageCount, 1), 4)
    const batchId = crypto.randomUUID()
    const slots = Array.from({ length: count }, () => ({ key: crypto.randomUUID(), jobId: null, submitError: '' }))
    // 同一节点一次只摆一批（与 Desktop 一样：新批次开始前先撤掉旧批的候选）。
    // 撤的只是**卡片**——旧批的产物文件不删、任务也不取消；重开一批时那几张已出好的图仍在任务里，
    // 只是不再有采用入口（这一点在下面那句提示里说清楚）。
    const replaced = candidateBatches.some(batch => batch.nodeId === nodeId)
    setCandidateBatches(current => [
      ...current.filter(batch => batch.nodeId !== nodeId),
      { batchId, nodeId, startedAt: Date.now(), slots }
    ])
    setNotice({
      kind: 'info',
      message: replaced
        ? `上一批候选卡已撤下（产物文件保留），已摆好 ${count} 个新候选位，正在提交生成…`
        : `已在原节点旁摆好 ${count} 个候选位，正在提交生成…`
    })

    for (const slot of slots) {
      const mark = (patch: { jobId?: string; submitError?: string }) => {
        setCandidateBatches(current => current.map(batch => batch.batchId !== batchId ? batch : {
          ...batch,
          slots: batch.slots.map(item => item.key === slot.key ? { ...item, ...patch } : item)
        }))
      }
      try {
        const body = {
          ...buildInvocationBody(imageSkill.id, prompt, DEFAULT_VIDEO_PARAMS, crypto.randomUUID()),
          targetNodeId: nodeId,
          candidateBatch: true,
          ...(aspectRatio.length > 0 ? { aspectRatio } : {})
        }
        const job = await request<WebJob>(`/api/web/skills/${encodeURIComponent(imageSkill.id)}/invoke`,
          { method: 'POST', body: JSON.stringify(body) })
        mark({ jobId: job.jobId })
      } catch (error) {
        mark({ submitError: errorMessage(error) })
      }
    }
    // 立刻取一次任务列表：新任务要等下一次轮询才出现，而按下生成的人正等着看状态。
    void refreshJobs()
  }

  /**
   * 采用一件候选产物。
   *
   * 客户端只说「哪个任务的第几件产物」——目标节点与产物文件都由服务端从任务自己那份输入里取，
   * 归属、权限、租约、节点锁定、目录与图片类型逐层校验，写入走受保护的追加（见 /adopt 那条路由）。
   * 幂等由确定性附件 ID 保证：重复点、或别人先挂上了，都不会多出一件。
   */
  async function adoptCandidate(nodeId: string, jobId: string, outputIndex: number) {
    if (!auth?.user || taskBusy) return
    setTaskBusy(true)
    try {
      const result = await request<{ alreadyAttached?: boolean }>(
        `/api/web/jobs/${encodeURIComponent(jobId)}/outputs/${outputIndex}/adopt`, { method: 'POST' })
      // 这一格已经从「等着采用」变成「节点上的产物」：把它从候选里摘掉（整批空了就撤掉这一批）。
      setCandidateBatches(current => current
        .map(batch => ({ ...batch, slots: batch.slots.filter(slot => slot.jobId !== jobId) }))
        .filter(batch => batch.slots.length > 0))
      await loadScene({ select: nodeId })
      setNotice({
        kind: 'success',
        message: result?.alreadyAttached ? '这一张已经在节点上了（重复采用不会重复添加）。' : '已采用：图片已追加到原节点。'
      })
    } catch (error) {
      // 目标已删除 / 锁定 / 权限失效等都在这里如实说清楚，产物仍留在任务里。
      setNotice({ kind: 'error', message: `采用失败：${errorMessage(error)}` })
    } finally { setTaskBusy(false) }
  }

  /**
   * 丢弃一批候选：只把画布上那排卡片撤掉。
   *
   * **不是取消任务**（要停生成请到任务上取消），也不动产物文件——这一层没有引用计数，
   * 删文件有可能删掉别处正在用的东西。生成中丢弃之后迟到的结果不会自动回挂、也不会再出现：
   * 候选任务在服务端那条路上本来就不自动回挂，而卡片只活在这次会话的内存里。
   */
  function discardCandidates(batchId: string) {
    setCandidateBatches(current => current.filter(batch => batch.batchId !== batchId))
    setNotice({ kind: 'info', message: '已撤掉这排候选卡：后台任务照旧，产物文件也没有删除。' })
  }

  /**
   * 右键菜单里那两行「出图选项」：张数（一次提交几张 = 几个候选位），
   * 以及比例——**只有技能自己声明了可选比例时**才摆（图片 API 会声明，本机工作流不会）。
   * 没有可跑的出图条目就不摆，免得菜单上多一行用不上的东西。
   */
  const imageMenuOptions = (() => {
    const imageSkill = skills.find(skill => skill.capability === 'TextToImage')
    if (!imageSkill || !editable) return null
    if (!nodeMenu?.plan.suggestions.some(suggestion => suggestion.kind === 'Image' && suggestion.canRun)) return null
    return {
      count: imageCount,
      onCount: setImageCount,
      ratios: imageSkill.aspectRatios ?? [],
      ratio: imageRatio,
      onRatio: setImageRatio
    }
  })()

  /**
   * 右键菜单里的「出视频选项」：时长（+ 后端支持时的边长）。
   *
   * 只在**这条视频建议真能跑**时才摆（计划说能跑、且网页端有视频技能）：一条点了没用的参数行，
   * 比没有这行更让人困惑。取值范围与换算直接用 videoParams 那一份——与请求体用的是同一份，
   * 免得界面报一个时长、发出去的又是另一个（「1 秒」实际是 1.04 秒这件事，只有那一处算得对）。
   *
   * 边长那一行按**后端**摆：接口站那条路不接受画幅，摆出来只会给一个发不出去的档位。
   */
  const videoMenuOptions = (() => {
    const videoSkill = skills.find(skill => skill.capability === 'TextToVideo')
    if (!videoSkill || !editable) return null
    if (!nodeMenu?.plan.suggestions.some(suggestion =>
      suggestion.kind === 'Video' && suggestion.canRun && suggestion.id !== PRODUCT_VIDEO_SUGGESTION_ID)) return null
    const backend = videoBackendOf(videoSkill)
    return {
      params: videoParams,
      backend,
      sizes: videoSizeOptions(backend),
      onSeconds: (seconds: string) => setVideoParams(current => ({ ...current, seconds })),
      onSize: (size: string) => setVideoParams(current => ({ ...current, size }))
    }
  })()

  /**
   * 结构级写入：把两个节点连起来。两个手势走的是同一条路——工具栏「连接」（点起点 → 点终点）
   * 与卡片右缘那个圆点（按住拖过去）——所以统一收成「起点 + 终点」两个参数，
   * 不让手势的差别渗进写入逻辑。
   *
   * 「许不许连」（自环、重复）由**服务端**用共享规则判——界面不先猜一遍：
   * 猜错了会把一次合法操作挡在门外，而界面上的判断没有一个会被回归钉住。
   * 失败的原因照服务端那句话显示（「这条连线已经存在」比「操作失败」有用得多）。
   */
  async function createEdge(sourceId: string, targetId: string) {
    if (!scene || !sourceId) return
    // 先退出连接模式：这一次点击的目的已经用掉了。失败也退——留在模式里，下一次点击会重复同一件事。
    setConnecting(false)
    if (!canEdit(role)) {
      setNotice({ kind: 'error', message: '你的账号是只读，改不了画布。' })
      return
    }

    setSaving(true)
    setNotice({ kind: 'info', message: '正在连接…' })
    try {
      const result = await request<{ revision: number; edgeId: string }>('/api/web/edges', {
        method: 'POST',
        body: JSON.stringify({ baseRevision: scene.revision, sourceId, targetId })
      })
      if (!result?.edgeId) throw new Error('连接响应里没有连线 ID')
      await loadScene({ select: sourceId })
      setNotice({ kind: 'success', message: `已连接，画布修订 ${result.revision}。` })
    } catch (error) {
      setNotice({ kind: 'error', message: `连接失败：${errorMessage(error)}` })
    } finally {
      setSaving(false)
    }
  }

  /** 结构级写入：确认后断开连线并保存到普通编辑历史。 */
  async function disconnectEdge(edgeId: string) {
    if (!scene) return
    if (!canEdit(role)) {
      setNotice({ kind: 'error', message: '你的账号是只读，改不了画布。' })
      return
    }
    if (!window.confirm('断开这条连线？')) return

    setSaving(true)
    setNotice({ kind: 'info', message: '正在断开…' })
    try {
      await request(`/api/web/edges/${encodeURIComponent(edgeId)}?baseRevision=${scene.revision}`, { method: 'DELETE' })
      // 断开之后停在原来那个节点上：连着看几条连线的人不必每断一条就重新点一遍。
      await loadScene({ select: selected?.recordId })
      setNotice({ kind: 'info', message: '已断开连线。' })
    } catch (error) {
      setNotice({ kind: 'error', message: `断开失败：${errorMessage(error)}` })
    } finally {
      setSaving(false)
    }
  }

  /**
   * 位置写入：在画布上把节点拖到别处，松手时调它。**记录级**写入，与「改标题内容」同一档
   * （服务端不要整棵树锁），所以拖一张卡片不会把别人的结构改动挡在门外。
   *
   * 先乐观地把卡片放在松手的位置：请求要跑几十毫秒，不先落地的话卡片会先弹回旧位置再跳过去。
   * 请求回来用服务端那份覆盖；失败就撤回原处——不然界面会一直显示一个没落盘的位置。
   * （撤回这一步本身就是「失败如实反馈」：位置回到服务端那一份，再加上一句说明。）
   */
  async function moveNode(recordId: string, x: number, y: number) {
    const current = scene
    if (!current) return
    const previous = current.records.find((item) => item.recordId === recordId)
    if (!previous) return
    // 拖出去又拖回来、或者往左拖被夹在 0：坐标没变就别发这次请求（服务端也会当成无需改动）。
    if (nodeX(previous) === x && nodeY(previous) === y) return
    if (!canEdit(role)) {
      setNotice({ kind: 'error', message: '你的账号是只读，改不了画布。' })
      return
    }

    const baseRevision = current.revision
    // 「把这一份记录换进场景里」——乐观更新与失败撤回共用它，免得两处各写一遍替换逻辑。
    const swap = (record: ViewRecord) => setScene((now) => now
      ? { ...now, records: now.records.map((item) => item.recordId === recordId ? record : item) }
      : now)
    swap({ ...previous, record: { ...previous.record, x, y } })
    setNotice({ kind: 'info', message: '正在写入位置…' })
    try {
      const result = await request<{ revision: number; record: ViewRecord }>(
        `/api/web/records/${encodeURIComponent(recordId)}/position`,
        { method: 'PUT', body: JSON.stringify({ baseRevision, x, y }) })
      if (!Number.isFinite(result?.revision) || !result.record || result.record.recordId !== recordId)
        throw new Error('移动响应格式不正确')
      setScene((now) => now
        ? { ...now, revision: result.revision, records: now.records.map((item) => item.recordId === recordId ? result.record : item) }
        : now)
      setLastSaved(new Date().toLocaleTimeString('zh-CN', { hour12: false }))
      // 位置变了，整理预览的基准修订随之失效——与保存同理，不能留着让人点「应用整理」才发现。
      setLayoutPlan(null)
      setNotice({ kind: 'success', message: `已移动「${recordTitle(result.record)}」，画布修订 ${result.revision}。` })
    } catch (error) {
      swap(previous)
      setNotice({
        kind: 'error',
        message: `移动失败：${errorMessage(error)}。位置已回到原处；修订冲突时请重新加载画布。`
      })
    }
  }

  /**
   * 改选中节点的类别。同样是**记录级**写入（与改标题内容 / 移动位置同一档，不占树锁）。
   *
   * 它**不进草稿**：下拉选一下就写一次。所以这里直接读「有没有草稿」是没意义的——
   * 判断的是「有没有人在编辑这个节点」和「我能不能改」。失败时界面上的下拉会退回服务端那一份
   * （它由 scene 里的 recordType 驱动，而不是本地状态），不需要额外的撤回代码。
   */
  async function changeCategory(recordType: string) {
    const target = selected
    if (!scene || !target || saving) return
    if (recordType === target.recordType) return
    if (blockedBy) {
      setNotice({ kind: 'error', message: `${describeLease(blockedBy)} 正在编辑这个节点，等他保存或让管理员接管。` })
      return
    }
    if (!canEdit(role)) {
      setNotice({ kind: 'error', message: '你的账号是只读，改不了画布。' })
      return
    }

    setSaving(true)
    setNotice({ kind: 'info', message: '正在改类别…' })
    try {
      const result = await request<{ revision: number; record: ViewRecord }>(
        `/api/web/records/${encodeURIComponent(target.recordId)}/category`,
        { method: 'PUT', body: JSON.stringify({ baseRevision: scene.revision, recordType }) })
      if (!Number.isFinite(result?.revision) || !result.record || result.record.recordId !== target.recordId)
        throw new Error('改类别响应格式不正确')
      setScene((now) => now
        ? { ...now, revision: result.revision, records: now.records.map((item) => item.recordId === target.recordId ? result.record : item) }
        : now)
      setLastSaved(new Date().toLocaleTimeString('zh-CN', { hour12: false }))
      // 类别变了，整理布局的落点也跟着变，旧预览的基准修订一并失效。
      setLayoutPlan(null)
      setNotice({
        kind: 'success',
        message: `已把「${recordTitle(result.record)}」改成${NODE_KINDS[kindOf(recordType)].label}，画布修订 ${result.revision}。`
      })
    } catch (error) {
      setNotice({ kind: 'error', message: `改类别失败：${errorMessage(error)}。下拉已退回原类别；修订冲突时请重新加载画布。` })
    } finally {
      setSaving(false)
    }
  }

  /**
   * 记录级写入：给当前选中的节点挂一条设定（从项目库里挑的那条）。
   *
   * 与「改标题内容」「移动位置」「改类别」同一档——引用是节点内容的一部分，不动画布结构。
   * **不做乐观更新**：挂不挂得上要服务端说了算（重复、变体 / 版本对不上都由它拦），
   * 所以等结果回来再重取画布。这类动作一次一个，不值得为它做本地推断。
   */
  async function addReference(entityId: string, variantId: string | null) {
    const target = selected
    if (!scene || !target) return
    if (!editable) {
      setNotice({ kind: 'error', message: '你的账号是只读，改不了画布。' })
      return
    }
    setSaving(true)
    setNotice({ kind: 'info', message: '正在挂上设定…' })
    try {
      await request(`/api/web/records/${encodeURIComponent(target.recordId)}/references`, {
        method: 'POST',
        body: JSON.stringify({ baseRevision: scene.revision, entityId, variantId })
      })
      await loadScene()
      setNotice({ kind: 'success', message: '已挂上这条设定。' })
    } catch (error) {
      setNotice({ kind: 'error', message: `挂上失败：${errorMessage(error)}` })
    } finally {
      setSaving(false)
    }
  }

  async function registerAttachment(attachmentId: string, entityId: string, variantId: string) {
    if (!scene || !selected || !editable || saving || dirty) return
    const nodeId = selected.recordId
    setSaving(true)
    try {
      const result = await request<{ registered: boolean; version: number }>(`/api/web/records/${encodeURIComponent(nodeId)}/attachments/${encodeURIComponent(attachmentId)}/register`, {
        method: 'POST', body: JSON.stringify({ baseRevision: scene.revision, entityId, variantId })
      })
      await loadScene({ select: nodeId })
      setNotice({ kind: 'success', message: result.registered ? `图片已登记并提交 v${result.version}，历史版本已保留。` : '图片已经登记，未重复提交版本。' })
    } catch (error) { setNotice({ kind: 'error', message: `登记失败：${errorMessage(error)}` }) }
    finally { setSaving(false) }
  }

  async function splitChapters() {
    if (!scene || !selected || !editable || saving || dirty) return
    if (!window.confirm('按所选节点的真实正文追加章节和出场节点？重复执行会追加另一批章节。')) return
    setSaving(true)
    try {
      await request('/api/web/worktree/split', { method: 'POST', body: JSON.stringify({ baseRevision: scene.revision, sourceId: selected.recordId }) })
      await loadScene({ select: selected.recordId })
      setNotice({ kind: 'success', message: '已按真实正文拆章并保存工作树。' })
    } catch (error) { setNotice({ kind: 'error', message: errorMessage(error) }) }
    finally { setSaving(false) }
  }

  async function syncWorkTree() {
    if (!scene || !editable || saving || dirty) return
    setSaving(true)
    try {
      const preview = await request<{ revision: number; plan: { baselineHash: string; changes: { reason: string; before: string; after: string; requiresConfirmation: boolean }[]; conflicts: { detail: string; blocking: boolean }[] } }>('/api/web/worktree/plan?direction=Both')
      const lines = preview.plan.changes.map(c => `${c.requiresConfirmation ? '待确认：' : ''}${c.reason}\n${c.before} → ${c.after}`)
      lines.push(...preview.plan.conflicts.map(c => `冲突：${c.detail}`))
      if (preview.plan.conflicts.some(c => c.blocking)) {
        setNotice({ kind: 'error', message: lines.join('\n') })
        return
      }
      if (!preview.plan.changes.length) {
        setNotice({ kind: 'success', message: '工作树与画布已同步。' })
        return
      }
      if (!window.confirm(`应用以下同步计划？\n${lines.join('\n')}`)) return
      await request('/api/web/worktree/apply', { method: 'POST', body: JSON.stringify({ baseRevision: preview.revision, direction: 'Both', baselineHash: preview.plan.baselineHash, confirmed: true }) })
      await loadScene({ select: selectedId })
      setNotice({ kind: 'success', message: '工作树同步已保存。' })
    } catch (error) { setNotice({ kind: 'error', message: errorMessage(error) }) }
    finally { setSaving(false) }
  }

  async function editHistory(operation: 'undo' | 'redo') {
    if (!scene || !editable || saving || loading || layoutBusy || dirty || historyInFlight.current ||
        !editHistoryState || editHistoryState.revision !== scene.revision || editHistoryState[operation] <= 0 ||
        leases.some(lease => lease.userId !== myUserId)) return
    historyInFlight.current = true
    setSaving(true)
    setEditHistoryState(null)
    try {
      await request(`/api/web/canvas/history/${operation}`, { method: 'POST', body: JSON.stringify({ baseRevision: scene.revision }) })
      await loadScene({ select: selectedId })
      setNotice({ kind: 'success', message: operation === 'undo' ? '已撤销普通编辑。' : '已重做普通编辑。' })
    } catch (error) { setNotice({ kind: 'error', message: errorMessage(error) }) }
    finally { historyInFlight.current = false; setSaving(false) }
  }

  return (
    <>
    <WorkbenchShell
      chrome={chrome}
      status={status}
      presence={<PresenceChip people={onlinePeople} />}
      session={<SessionChip state={auth} onChanged={() => void refreshSession()} onLogout={() => void logout()} />}
      tree={(
        <>
        {section === 'project' && <details className="df-section">
          <summary className="df-heading">项目资源库</summary>
          <EntityManager canEdit={canEdit(role)} onChanged={async () => { const result = await request('/api/web/assets'); setAssets(mapAssets(result).entities); setAssetsReady(true) }} />
        </details>}
        <ChapterTree
          groups={groups}
          activeChapter={activeChapter}
          selectedId={selectedId}
          onChapter={setActiveChapter}
          onSelect={selectRecord}
          onCreate={editable && !saving ? (chapterId, recordType) => void createNode(chapterId === PLANNING_GROUP_ID || chapterId === UNFILED_GROUP_ID ? ALL_CHAPTERS_ID : chapterId, recordType) : undefined}
          onSync={editable && !saving && !dirty ? () => void syncWorkTree() : undefined}
          onSplit={editable && !saving && !dirty && selected && isEditableRecord(selected) && !!recordContent(selected).trim() ? () => void splitChapters() : undefined}
        />
        </>
      )}
      workspace={(
        <Workspace
          view={view}
          records={records}
          selectedId={selectedId}
          onSelect={selectRecord}
          search={search}
          canvasTitle={chrome.canvasTitle}
          activeChapter={activeChapter}
          onChapter={setActiveChapter}
          readOnly={readOnly}
          canEdit={canEdit(role)}
          assets={assets}
          assetsReady={assetsReady}
          leases={leases}
          myUserId={myUserId}
          channelNotice={channelNotice}
          layoutBusy={layoutBusy}
          layoutPlan={layoutPlan}
          jobs={jobs}
          candidates={candidateViews}
          onAdoptCandidate={(nodeId, jobId, outputIndex) => void adoptCandidate(nodeId, jobId, outputIndex)}
          onDiscardCandidates={discardCandidates}
          onOpenCandidate={(jobId, outputIndex, name) => setCandidateFocus({ jobId, outputIndex, name })}
          agentPreview={agentPreview?.revision === scene?.revision && !remoteChange ? agentPreview : null}
          layoutScope={layoutScope}
          onLayoutPlan={(scope) => void planLayout(scope)}
          onLayoutApply={(overrideManual) => void applyLayout(overrideManual)}
          onLayoutCancel={() => setLayoutPlan(null)}
          edges={edges}
          connectFrom={connectFrom}
          connectBlocked={connectBlocked}
          onConnectStart={() => setConnecting(true)}
          onConnectCancel={() => setConnecting(false)}
          onConnectTarget={(targetId) => { if (connectFrom) void createEdge(connectFrom, targetId) }}
          onConnectNodes={(sourceId, targetId) => void createEdge(sourceId, targetId)}
          onNodeContextMenu={(recordId, position) => void openNodeMenu(recordId, position)}
          // 点引用徽标：选中那个节点并把这条引用的详情展开（只影响展示，不写任何配置）。
          onOpenReference={(recordId, reference) => {
            setDockOpen(true)
            setDockMode('inspector')
            setReferenceFocus({ recordId, key: referenceKey(reference), nonce: Date.now() })
            selectRecord(recordId)
          }}
          // 点卡片上节点自己出的图：同样先选中它，再让检查器打开这张图的大图。
          onOpenMedia={(recordId, attachmentId) => {
            setDockOpen(true)
            setDockMode('inspector')
            setMediaFocus({ recordId, attachmentId, nonce: Date.now() })
            selectRecord(recordId)
          }}
          // 拖动与「改标题内容」同一档（记录级）：角色可编辑、画布不是只读就能拖；
          // 单个节点还会再看锁——那一条在画布里判，因为它要看每张卡各自的锁。
          draggable={editable}
          onMove={(recordId, x, y) => void moveNode(recordId, x, y)}
          onCreate={editable ? recordType => void createNode(groups.some(g => g.id === activeChapter) && activeChapter !== PLANNING_GROUP_ID && activeChapter !== UNFILED_GROUP_ID ? activeChapter : ALL_CHAPTERS_ID, recordType) : undefined}
          onDelete={editable && selected && isEditableRecord(selected) ? () => void deleteNode() : undefined}
          editBusy={saving || loading || layoutBusy}
          onUndo={editable && !dirty && editHistoryState?.revision === scene?.revision && (editHistoryState?.undo ?? 0) > 0 && !leases.some(lease => lease.userId !== myUserId) ? () => void editHistory('undo') : undefined}
          onRedo={editable && !dirty && editHistoryState?.revision === scene?.revision && (editHistoryState?.redo ?? 0) > 0 && !leases.some(lease => lease.userId !== myUserId) ? () => void editHistory('redo') : undefined}
        />
      )}
      dock={(
        <div hidden={!dockOpen} style={{ height: '100%' }}><RightDock mode={dockMode} onMode={setDockMode} onClose={() => {
          if (dockMode === 'agent') setDockMode('inspector')
          else setDockOpen(false)
        }}>
          {dockMode === 'inspector'
            ? (
              <InspectorPanel
                selected={selected}
                records={records}
                title={title}
                content={content}
                dirty={dirty}
                saving={saving}
                readOnly={readOnly}
                canEdit={canEdit(role)}
                hint={inspectorHint}
                blockedBy={blockedBy}
                heldByMe={heldByMe}
                onEditingChange={setEditorFocused}
                assets={assets}
                assetsReady={assetsReady}
                revision={scene?.revision}
                focusReference={referenceFocus}
                focusMedia={mediaFocus}
                jobs={jobs}
                jobsError={jobsError}
                onRegisterAttachment={editable ? (attachmentId, entityId, variantId) => void registerAttachment(attachmentId, entityId, variantId) : undefined}
                onTitle={setTitle}
                onContent={setContent}
                onApply={() => void save()}
                onDelete={canEdit(role) && selected !== null && isEditableRecord(selected) ? () => void deleteNode() : undefined}
                edges={edges}
                // 断开连线与「删除节点」同一档：都要编辑权限。不给就只显示列表、不显示按钮。
                onDisconnect={editable ? (edgeId) => void disconnectEdge(edgeId) : undefined}
                // 改类别也是记录级，与「改标题内容」同一档：能编辑就给。
                onCategory={editable ? (recordType) => void changeCategory(recordType) : undefined}
                // 挂设定同样是记录级：能编辑就给。挂完要重新取画布——引用是服务端算出来的那份投影。
                onAddReference={editable ? (entityId, variantId) => void addReference(entityId, variantId) : undefined}
              />
            )
            : dockMode === 'settings'
              ? <SettingsPanel canManage={role === 'Admin'} />
              : null}
              <div hidden={dockMode !== 'agent'} style={{ height: '100%' }}><AgentPanel
                userId={myUserId}
                canChat={canEdit(role)}
                chapterId={activeChapter === ALL_CHAPTERS_ID ? undefined : activeChapter}
                nodeId={selected && isEditableRecord(selected) ? selected.recordId : undefined}
                baseRevision={scene?.revision}
                // 审批详情的「变更前」就是这份正式场景投影：预检通过时它的修订与预览一致
                // （修订一变，ChatPanel 会先把预览收掉），所以按 recordId 取出来的一定是变更前的那一份。
                records={records}
                canCommit={editable && !dirty && !remoteChange && !saving && !loading}
                nodeAgentRequest={nodeAgentRequest}
                onNodeAgentConsumed={id => setNodeAgentRequest(current => current?.id === id ? null : current)}
                onPreviewChange={preview => { setAgentPreview(preview); if (preview) setView('canvas') }}
                onChanged={async () => { const next = parseScene(await request('/api/web/scene')); setScene(next); setRemoteChange(null); await refreshLeases() }}
              /></div>
        </RightDock></div>
      )}
      badge={{
        visible: !chrome.agentActive,
        text: 'AI',
        onClick: () => { setDockOpen(true); setDockMode('agent') }
      }}
    />
    {nodeMenu && (
      <NodeContextMenu
        x={nodeMenu.x}
        y={nodeMenu.y}
        items={buildNodeMenu(nodeMenu.plan, {
          imageSkillId: skills.find(skill => skill.capability === 'TextToImage')?.id ?? null,
          videoSkillId: skills.find(skill => skill.capability === 'TextToVideo')?.id ?? null,
          canRunAgent: editable
        })}
        imageOptions={imageMenuOptions}
        videoOptions={videoMenuOptions}
        onPick={(item) => void runNodeMenuItem(item)}
        onClose={() => setNodeMenu(null)}
      />
    )}
    {candidateFocus && (
      <div className="df-ref-viewer" role="dialog" aria-label="候选产物大图" onPointerDown={() => setCandidateFocus(null)}>
        <img
          src={jobOutputUrl(candidateFocus.jobId, candidateFocus.outputIndex)}
          alt={candidateFocus.name}
          onPointerDown={(event) => event.stopPropagation()}
        />
        <div className="df-ref-viewer-bar" onPointerDown={(event) => event.stopPropagation()}>
          <span>{candidateFocus.name}</span>
          <button type="button" className="df-mini-button" onClick={() => setCandidateFocus(null)}>关闭（Esc）</button>
        </div>
      </div>
    )}
    </>
  )
}

/**
 * 节点右键菜单。**只有摆与收起**：菜单项从哪来（服务端那份协助计划）与点下去做什么
 * （`runNodeMenuItem`）都在外面——这样它既不知道画布数据，也不知道写路径。
 *
 * 三条消失路径都要有（Esc / 点别处 / 滚动）：菜单是浮在画布上的，没有一条明确的消失路径时，
 * 用户会去点它、然后以为点不动。
 */
function NodeContextMenu({ x, y, items, imageOptions, videoOptions, onPick, onClose }: {
  x: number
  y: number
  items: NodeMenuItem[]
  /**
   * 出图选项（张数 / 比例）。null = 这个节点没有可跑的出图条目，这一块不摆。
   * 张数决定一次提交几张、原节点旁就有几个候选位；比例只有技能自己声明了可选值时才出现。
   */
  imageOptions: {
    count: number
    onCount: (count: number) => void
    ratios: string[]
    ratio: string
    onRatio: (ratio: string) => void
  } | null
  /**
   * 出视频选项（时长 / 后端支持时的边长）。null = 这个节点没有可跑的出视频条目。
   * 换算与取值范围由 videoParams 定：这里只摆档位，不自己折秒与帧。
   * `sizes` 为空表示这个后端不接受画幅（接口站那条路），边长那一行就不摆。
   */
  videoOptions: {
    params: VideoParams
    backend: VideoBackend
    sizes: string[]
    onSeconds: (seconds: string) => void
    onSize: (size: string) => void
  } | null
  onPick: (item: NodeMenuItem) => void
  onClose: () => void
}) {
  const menuRef = useRef<HTMLDivElement>(null)
  // 右键点靠近窗口下沿时，菜单会整块挂在视口外，下半截（含「让 Agent…」）点不到。
  // 先按原始位置量一次真实尺寸，再把 left/top 夹进视口；比视口还高时给它一个可滚动上限。
  const [pos, setPos] = useState<{ left: number; top: number; maxHeight: number | null }>({ left: x, top: y, maxHeight: null })
  useLayoutEffect(() => {
    const menu = menuRef.current
    if (!menu) return
    const margin = 8
    const maxHeight = window.innerHeight - margin * 2
    const height = Math.min(menu.offsetHeight, maxHeight)
    setPos({
      left: Math.max(margin, Math.min(x, window.innerWidth - menu.offsetWidth - margin)),
      top: Math.max(margin, Math.min(y, window.innerHeight - height - margin)),
      maxHeight: menu.offsetHeight > maxHeight ? maxHeight : null
    })
  }, [x, y])

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    // 用捕获阶段关闭菜单外的点击；菜单内的按下必须保留给按钮，否则菜单会在 click 前被卸载。
    const onDown = (event: Event) => {
      if (menuRef.current?.contains(event.target as Node)) return
      onClose()
    }
    window.addEventListener('keydown', onKey)
    window.addEventListener('pointerdown', onDown, true)
    window.addEventListener('wheel', onDown, { passive: true })
    return () => {
      window.removeEventListener('keydown', onKey)
      window.removeEventListener('pointerdown', onDown, true)
      window.removeEventListener('wheel', onDown)
    }
  }, [onClose])

  return (
    <div ref={menuRef} className="df-menu" style={{ left: pos.left, top: pos.top, maxHeight: pos.maxHeight ?? undefined, overflowY: pos.maxHeight ? 'auto' : undefined }} onPointerDown={(event) => event.stopPropagation()}>
      {imageOptions && (
        <div className="df-menu-row">
          <span className="df-menu-row-label">张数</span>
          {[1, 2, 3, 4].map(value => (
            <button
              key={value}
              type="button"
              className={`df-mini-button${imageOptions.count === value ? ' is-active' : ''}`}
              title={`一次提交 ${value} 张，原节点旁就有 ${value} 个候选位`}
              onClick={() => imageOptions.onCount(value)}
            >{value}</button>
          ))}
        </div>
      )}
      {imageOptions && imageOptions.ratios.length > 0 && (
        <div className="df-menu-row">
          <span className="df-menu-row-label">比例</span>
          {imageOptions.ratios.map(ratio => (
            <button
              key={ratio}
              type="button"
              className={`df-mini-button${imageOptions.ratio === ratio ? ' is-active' : ''}`}
              onClick={() => imageOptions.onRatio(ratio)}
            >{ratio}</button>
          ))}
        </div>
      )}
      {videoOptions && (
        <div className="df-menu-row">
          <span className="df-menu-row-label">时长</span>
          {['1', '2', '3', '4'].map(value => (
            <button
              key={value}
              type="button"
              className={`df-mini-button${videoOptions.params.seconds === value ? ' is-active' : ''}`}
              // LTX 那条路秒折成帧之后实际会比所求略长（只认 1+8k 帧），这里就把那件事说清楚；
              // 接口站那条路没有帧这一步，秒数就是秒数。
              title={videoOptions.backend === 'http' ? `${value} 秒` : `约 ${videoEffectiveSeconds(value).toFixed(2)} 秒`}
              onClick={() => videoOptions.onSeconds(value)}
            >{value}s</button>
          ))}
        </div>
      )}
      {videoOptions && videoOptions.sizes.length > 0 && (
        <div className="df-menu-row">
          <span className="df-menu-row-label">边长</span>
          {videoOptions.sizes.map(size => (
            <button
              key={size}
              type="button"
              className={`df-mini-button${videoOptions.params.size === size ? ' is-active' : ''}`}
              onClick={() => videoOptions.onSize(size)}
            >{size}</button>
          ))}
        </div>
      )}
      {items.map((item, index) => (
        <span key={item.id}>
          {/* 分组之间切一条线：上面是「这个节点能做什么」，下面才是「对节点本身做什么」。 */}
          {index > 0 && items[index - 1].group !== item.group && <span className="df-menu-sep" />}
          {/* 灰着的原因既写在 title 上、也显示在项里：不说原因的话，「点不动」只会被读成菜单坏了。 */}
          <button
            type="button"
            className="df-menu-item"
            disabled={!item.enabled}
            title={item.reason ?? item.label}
            onClick={() => onPick(item)}
          >
            {item.label}
            {item.reason && <span className="df-menu-reason">{item.reason}</span>}
          </button>
        </span>
      ))}
    </div>
  )
}

/** 标题栏右侧的账号区：保留身份摘要，并复用会话面板里的账号操作。 */
function SessionChip({ state, onChanged, onLogout }: { state: AuthState; onChanged: () => void; onLogout: () => void }) {
  const user = state.user!
  const name = user.displayName || user.username
  const initials = name.slice(0, 2).toUpperCase()
  const dialog = useRef<HTMLDialogElement>(null)
  const [accountOpen, setAccountOpen] = useState(false)
  return (
    <span className="df-session">
      <span className="df-avatar" aria-hidden="true">{initials}</span>
      <span className="df-stack" style={{ alignItems: 'flex-start', lineHeight: 1.3 }}>
        <span style={{ fontSize: 12, color: 'var(--df-ink)' }}>{name}</span>
        <span className="df-dim" style={{ fontSize: 10 }}>{roleLabel(user.role)}</span>
      </span>
      <button type="button" className="df-tool-button" aria-haspopup="dialog" onClick={() => { setAccountOpen(true); dialog.current?.showModal() }}>账号</button>
      <dialog ref={dialog} className="df-account-dialog" aria-label="账号设置" onClose={() => setAccountOpen(false)}>
        <div className="df-account-heading">
          <strong>账号设置</strong>
          <button type="button" className="df-tool-button" onClick={() => dialog.current?.close()}>关闭</button>
        </div>
        {accountOpen && <SessionPanel state={state} onChanged={onChanged} />}
      </dialog>
      <button type="button" className="df-tool-button" onClick={onLogout} title={`退出登录（${name} · ${roleLabel(user.role)}）`}>退出</button>
    </span>
  )
}

/**
 * 命令条上的「N 人在线」，点开能看名字与各自的端。
 *
 * 措辞全部走共享文案（两端同一份）；**在线 ≠ 拥有锁**：这里只列人，不带任何节点或锁的信息。
 * 名单为空时整块不渲染——「0 人在线」没有信息量，反而像出了故障。
 */
export function PresenceChip({ people }: { people: PresencePerson[] }) {
  if (people.length === 0) return null
  const online = people.filter((person) => person.basis === 'connection' && person.connections > 0)
  const count = String(online.length)
  const names = online.map((person) => person.displayName).join(uiText('separator'))
  // 名单里的端是服务端给的自由字符串；认不出的交给 clientLabel 按「网页端」算（与锁那条路同一条规则）。
  const clientTag = (client: string) => clientLabel(client === 'desktop' ? 'desktop' : client === 'web' ? 'web' : 'unknown')
  return (
    <details style={{ position: 'relative', display: 'inline-block' }}>
      <summary
        className="df-tool-button"
        style={{ listStyle: 'none', cursor: 'pointer' }}
        title={uiTextFill('presence.detail', { count, names })}
      >
        {uiTextFill('presence.count', { count })}
      </summary>
      <div
        style={{
          position: 'absolute', right: 0, top: '110%', zIndex: 40, minWidth: 160, padding: 8,
          background: 'var(--df-surface-2)', border: '1px solid var(--df-line)', borderRadius: 8
        }}
      >
        {people.map((person) => (
          <div key={person.userId} style={{ fontSize: 12, padding: '2px 0', display: 'flex', justifyContent: 'space-between', gap: 12 }}>
            <span>{person.displayName}（{person.basis === 'connection' && person.connections > 0 ? '在线' : person.basis === 'recent' ? '最近活跃' : '未确认在线'}）</span>
            {person.clients.length > 0 && (
              <span className="df-dim" style={{ fontSize: 10 }}>
                {person.clients.map((client) => clientTag(client)).join(uiText('separator'))}
              </span>
            )}
          </div>
        ))}
      </div>
    </details>
  )
}
