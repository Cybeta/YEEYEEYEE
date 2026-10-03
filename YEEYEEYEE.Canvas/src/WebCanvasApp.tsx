import { useEffect, useMemo, useRef, useState } from 'react'
import { mapAssets, recordReferences, resolveReference, type Asset } from './assets'
import { ALL_CHAPTERS_ID } from './ChapterView'
import { CANVAS_VERSION } from './Protocol/VersionedMessages'
import { SessionPanel } from './SessionPanel'
import { canEdit, gateOf, parseAuthState, roleLabel, type AuthState } from './SessionView'
import { AgentPanel, type WebJob, type WebSkill } from './shell/AgentPanel'
import { ChapterTree } from './shell/ChapterTree'
import { InspectorPanel } from './shell/InspectorPanel'
import { RightDock } from './shell/RightDock'
import { canvasBounds, chapterGroups, isEditableRecord, parseScene, recordContent, recordTitle, type ShellScene } from './shell/records'
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

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : '未知错误'
}

/**
 * 会话 cookie 由浏览器自动带上，所以这里**不再手动塞 Authorization 头**。
 * 凭据放在 HttpOnly cookie 里：脚本读不到它，也就不存在「前端把令牌存哪儿」这个问题。
 */
async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...options,
    credentials: 'same-origin',
    headers: { ...(options?.body ? { 'Content-Type': 'application/json' } : {}) },
    cache: 'no-store'
  })
  const data: unknown = await response.json().catch(() => null)
  if (!response.ok) {
    const failure = data && typeof data === 'object' ? data as { code?: unknown; message?: unknown } : {}
    throw new Error(`${typeof failure.code === 'string' ? `[${failure.code}] ` : ''}${typeof failure.message === 'string' ? failure.message : `HTTP ${response.status}`}`)
  }
  return data as T
}

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
  const generation = useRef(0)

  const records = useMemo(() => scene?.records ?? [], [scene])
  const groups = useMemo(() => chapterGroups(records), [records])
  const selected = useMemo(() => records.find((item) => item.recordId === selectedId) ?? null, [records, selectedId])
  const dirty = !!selected && (title !== recordTitle(selected) || content !== recordContent(selected))
  const readOnly = scene?.readOnly === true
  const role = auth?.user?.role ?? 'Viewer'
  const editable = canEdit(role) && !readOnly

  /**
   * 选中 / 取消选中。传 null 是「在画布上点了空白处」——桌面端也是按空白先取消选中再进入平移。
   * 切换与取消都要过一遍未保存确认：把编辑框里的草稿丢掉是同样的一件事。
   */
  function selectRecord(id: string | null) {
    const item = id === null ? null : records.find((record) => record.recordId === id) ?? null
    if (id !== null && item === null) return
    if (dirty && !window.confirm('当前编辑尚未保存，确定放弃修改吗？')) return
    setSelectedId(item?.recordId ?? '')
    setTitle(item ? recordTitle(item) : '')
    setContent(item ? recordContent(item) : '')
  }

  async function loadScene() {
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
      setNotice({
        kind: 'success',
        message: `画布加载成功：修订 ${parsed.revision}，${parsed.records.filter(isEditableRecord).length} 个节点、${parsed.records.length - parsed.records.filter(isEditableRecord).length} 条工作树章节。${parsed.readOnly ? ' 服务端把它标成了只读。' : ''}`
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
      setNotice({ kind: 'success', message: `已保存 ${id.slice(0, 8)}，画布修订 ${result.revision}。` })
    } catch (error) {
      setNotice({ kind: 'error', message: `保存失败：${errorMessage(error)}。修改仍在编辑框里；修订冲突时请重新加载画布。` })
    } finally {
      setSaving(false)
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
            : { label: '已同步', tone: 'ok' }

  const status: StatusFacts = {
    text: notice.message,
    revision: `修订 ${scene?.revision ?? '—'}`,
    syncLabel: sync.label,
    syncTone: sync.tone,
    nodes: `${nodeCount} 节点`,
    // 连线没有被投影到网页端（NodeProjection 只投影节点），所以这里如实写出来，
    // 而不是显示一个永远是 0 的漂亮数字。
    edges: '连线未投影',
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
    saveDisabled: !editable || !dirty || saving
  }

  const inspectorHint = !selected
    ? '在画布上点一个节点后可以改它的名称与内容'
    : readOnly ? '这张画布被服务端标成只读，改不了'
      : !canEdit(role) ? '你的账号是只读，改不了画布'
        : dirty ? '改完点「应用修改」或按 Ctrl+Enter 写回画布' : '没有未保存的修改'

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
          assets={assets}
          assetsReady={assetsReady}
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
                assets={assets}
                assetsReady={assetsReady}
                onTitle={setTitle}
                onContent={setContent}
                onApply={() => void save()}
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
