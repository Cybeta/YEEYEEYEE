import { useEffect, useMemo, useRef, useState } from 'react'
import { errorMessage, request } from './api'
import { mapAssets, recordReferences, resolveReference, type Asset } from './assets'
import { ALL_CHAPTERS_ID } from './ChapterView'
import { CANVAS_VERSION } from './Protocol/VersionedMessages'
import { SessionPanel } from './SessionPanel'
import { canEdit, gateOf, parseAuthState, roleLabel, type AuthState } from './SessionView'
import { AgentPanel, type WebJob, type WebSkill } from './shell/AgentPanel'
import { ChapterTree } from './shell/ChapterTree'
import { InspectorPanel } from './shell/InspectorPanel'
import { parseLayoutPlan, type LayoutPlan, type LayoutScope } from './shell/layoutPlan'
import { describeLease, leaseCovering, shouldHoldNodeLease } from './shell/locks'
import { RightDock } from './shell/RightDock'
import { canvasBounds, chapterGroups, isEditableRecord, kindOf, parseScene, recordContent, recordTitle, type ShellScene } from './shell/records'
import { canvasChangeText, followAction, hasUnsavedDraft, isOtherRevision } from './shell/serverEvents'
import { useLeases } from './shell/useLeases'
import { useNodeLease } from './shell/useNodeLease'
import { useServerEvents } from './shell/useServerEvents'
import { Workspace } from './shell/Workspace'
import {
  WorkbenchShell, type DockMode, type RailSection, type StatusFacts, type WorkbenchChrome, type WorkbenchView
} from './shell/WorkbenchShell'
import './session.css'
import './workflow.css'
// 令牌放最后：它定义的 :root 与 body 是整页的底色与字体，得压过前面那两份样式。
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
export { mapAssets, recordReferences, resolveReference } from './assets'

type Notice = { kind: 'success' | 'error' | 'info'; message: string }

export function WebCanvasApp() {
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
  const [skills, setSkills] = useState<WebSkill[]>([])
  const [jobs, setJobs] = useState<WebJob[]>([])
  const [prompt, setPrompt] = useState('')
  const [taskNotice, setTaskNotice] = useState('技能与任务尚未加载。')
  const [taskBusy, setTaskBusy] = useState(false)
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
  /** 服务端推来一条「画布变了」，且那个修订和我手上的不是同一个——我这份已经不是最新的了。 */
  const [remoteChange, setRemoteChange] = useState<{ revision: number; text: string } | null>(null)
  const generation = useRef(0)
  // 编辑锁读取：正常情况下靠推送立刻刷新，15 秒的轮询是兜底（推送断了、或锁因超时自然消失）。
  const { leases, error: leaseError, invalidCount: leaseInvalidCount, refresh: refreshLeases } = useLeases(!!auth?.user)

  const records = useMemo(() => scene?.records ?? [], [scene])
  const edges = useMemo(() => scene?.edges ?? [], [scene])
  const groups = useMemo(() => chapterGroups(records), [records])
  const selected = useMemo(() => records.find((item) => item.recordId === selectedId) ?? null, [records, selectedId])
  const dirty = !!selected && (title !== recordTitle(selected) || content !== recordContent(selected))
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

  // 变更推送：锁变了立刻刷新锁列表；画布被改了就把「已同步」改成「有新版」。
  // 推送只说「变了」，所以这里不自动重载画布——把整页视角与选中项一起抽走，比晚看到几秒更烦人。
  const events = useServerEvents({
    enabled: !!auth?.user,
    onEditsChanged: () => void refreshLeases(),
    onCanvasChanged: (event) => {
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
    const run = ++generation.current
    setLoading(true)
    setNotice({ kind: 'info', message: '正在加载画布…' })
    setScene(null)
    setAssets([])
    setAssetsReady(false)
    setSkills([])
    setJobs([])
    setTaskNotice('正在加载技能与任务…')
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
      const tasks = await request<{ jobs: WebJob[] }>('/api/web/jobs')
      if (run !== generation.current) return
      if (!Array.isArray(catalog.skills) || !Array.isArray(tasks.jobs)) throw new Error('响应格式不正确')
      setSkills(catalog.skills)
      setJobs(tasks.jobs)
      setTaskNotice(catalog.skills.length ? '技能已加载。' : '当前没有经服务端预授权的本机技能；任务仍可只读查看。')
    } catch (error) {
      if (run === generation.current) setTaskNotice(`技能或任务加载失败：${errorMessage(error)}`)
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
        setJobs([])
        setTaskNotice('技能与任务尚未加载。')
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

  useEffect(() => {
    if (!auth?.user) return
    let active = true
    const refresh = async () => {
      try {
        const result = await request<{ jobs: WebJob[] }>('/api/web/jobs')
        if (active) {
          if (!Array.isArray(result.jobs)) throw new Error('任务响应格式不正确')
          setJobs(result.jobs)
        }
      } catch (error) { if (active) setTaskNotice(`任务刷新失败：${errorMessage(error)}`) }
    }
    const timer = window.setInterval(() => void refresh(), 3000)
    return () => { active = false; window.clearInterval(timer) }
  }, [auth?.user?.id])

  // Esc 退出连接模式。只退模式、**不清选中**：清选中会牵出「未保存的修改要不要放弃」那一问，
  // 而按 Esc 的人想停的是「连线」这件事，不是想丢掉手上的草稿。
  useEffect(() => {
    if (!connecting) return
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') setConnecting(false) }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [connecting])

  async function taskAction(path: string, body?: object) {
    if (!auth?.user || taskBusy) return
    const run = generation.current
    setTaskBusy(true)
    try {
      const result = await request<WebJob>(path, { method: 'POST', ...(body ? { body: JSON.stringify(body) } : {}) })
      if (run !== generation.current) return
      setJobs((current) => [result, ...current.filter((job) => job.jobId !== result.jobId)])
      setTaskNotice(`任务 ${result.jobId}：${result.state}${result.errorMessage ? ` · ${result.errorMessage}` : ''}`)
    } catch (error) { if (run === generation.current) setTaskNotice(`任务操作失败：${errorMessage(error)}`) }
    finally { setTaskBusy(false) }
  }

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
            : dirty ? '改完点「应用修改」或按 Ctrl+Enter 写回画布' : '点进编辑框会先占锁，别人这时改不了这个节点'

  // 通道自己的问题。它不该把画布变成不可用，但也不能悄悄吞掉——
  // 否则界面上的「没有人编辑」会被读成「现在没人编辑」，「已同步」会被读成「推送通着」。
  const channelNotice = [
    nodeLeaseState.error ? `编辑锁：${nodeLeaseState.error}` : '',
    leaseError ? `编辑锁暂时读不到：${leaseError}` : '',
    leaseInvalidCount > 0 ? `有 ${leaseInvalidCount} 条锁记录格式不对，已跳过` : '',
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

  /** 结构级写入：删掉选中的节点（连带它的连线）。删之前问一句——服务端那边没有撤销。 */
  async function deleteNode() {
    const target = selected
    if (!scene || !target) return
    if (!canEdit(role)) {
      setNotice({ kind: 'error', message: '你的账号是只读，改不了画布。' })
      return
    }
    if (!window.confirm(`删掉「${recordTitle(target)}」？它的连线会一并删掉，服务端没有撤销。`)) return

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
   * 结构级写入：从连接模式的起点连到刚点的这个节点。
   *
   * 「许不许连」（自环、重复）由**服务端**用共享规则判——界面不先猜一遍：
   * 猜错了会把一次合法操作挡在门外，而界面上的判断没有一个会被回归钉住。
   * 失败的原因照服务端那句话显示（「这条连线已经存在」比「操作失败」有用得多）。
   */
  async function createEdge(targetId: string) {
    const sourceId = connectFrom
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

  /** 结构级写入：断开一根连线。断开之前问一句——服务端那边没有撤销。 */
  async function disconnectEdge(edgeId: string) {
    if (!scene) return
    if (!canEdit(role)) {
      setNotice({ kind: 'error', message: '你的账号是只读，改不了画布。' })
      return
    }
    if (!window.confirm('断开这条连线？服务端没有撤销。')) return

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

  return (
    <WorkbenchShell
      chrome={chrome}
      status={status}
      session={<SessionChip name={user.displayName || user.username} role={roleLabel(user.role)} onLogout={() => void logout()} />}
      tree={(
        <ChapterTree
          groups={groups}
          activeChapter={activeChapter}
          selectedId={selectedId}
          onChapter={setActiveChapter}
          onSelect={selectRecord}
          onCreate={(chapterId, recordType) => void createNode(chapterId, recordType)}
        />
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
          layoutScope={layoutScope}
          onLayoutPlan={(scope) => void planLayout(scope)}
          onLayoutApply={(overrideManual) => void applyLayout(overrideManual)}
          onLayoutCancel={() => setLayoutPlan(null)}
          edges={edges}
          connectFrom={connectFrom}
          connectBlocked={connectBlocked}
          onConnectStart={() => setConnecting(true)}
          onConnectCancel={() => setConnecting(false)}
          onConnectTarget={(targetId) => void createEdge(targetId)}
        />
      )}
      dock={dockOpen ? (
        <RightDock mode={dockMode} onMode={setDockMode} onClose={() => setDockOpen(false)}>
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
                onTitle={setTitle}
                onContent={setContent}
                onApply={() => void save()}
                onDelete={canEdit(role) && selected !== null && isEditableRecord(selected) ? () => void deleteNode() : undefined}
                edges={edges}
                // 断开连线与「删除节点」同一档：都要编辑权限。不给就只显示列表、不显示按钮。
                onDisconnect={editable ? (edgeId) => void disconnectEdge(edgeId) : undefined}
              />
            )
            : (
              <AgentPanel
                skills={skills}
                jobs={jobs}
                prompt={prompt}
                busy={taskBusy}
                canInvoke={!!auth.user}
                notice={taskNotice}
                onPrompt={setPrompt}
                onInvoke={(skillId) => void taskAction(`/api/web/skills/${encodeURIComponent(skillId)}/invoke`, { prompt, idempotencyKey: crypto.randomUUID() })}
                onCancel={(jobId) => void taskAction(`/api/web/jobs/${jobId}/cancel`)}
                onRetry={(jobId) => void taskAction(`/api/web/jobs/${jobId}/retry`)}
              />
            )}
        </RightDock>
      ) : null}
      badge={{
        visible: !dockOpen,
        text: 'AI',
        onClick: () => { setDockOpen(true); setDockMode('agent') }
      }}
    />
  )
}

/** 标题栏右侧的账号区：头像首字母 + 名字与角色 + 退出。 */
function SessionChip({ name, role, onLogout }: { name: string; role: string; onLogout: () => void }) {
  const initials = name.slice(0, 2).toUpperCase()
  return (
    <span className="df-session">
      <span className="df-avatar" aria-hidden="true">{initials}</span>
      <span className="df-stack" style={{ alignItems: 'flex-start', lineHeight: 1.3 }}>
        <span style={{ fontSize: 12, color: 'var(--df-ink)' }}>{name}</span>
        <span className="df-dim" style={{ fontSize: 10 }}>{role}</span>
      </span>
      <button type="button" className="df-tool-button" onClick={onLogout} title={`退出登录（${name} · ${role}）`}>退出</button>
    </span>
  )
}
