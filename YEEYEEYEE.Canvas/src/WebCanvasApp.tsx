import { useEffect, useRef, useState } from 'react'
import { chapterLabelOf } from './ChapterView'
import { layerOf, type OperationRecord } from './Protocol/VersionedMessages'
import { SessionPanel } from './SessionPanel'
import { canEdit, gateOf, parseAuthState, type AuthState } from './SessionView'
import './workflow.css'

type WebScene = { revision: number; records: OperationRecord[] }
type Version = { id: string; number?: number; label?: string }
type Variant = { id: string; name?: string; versions?: Version[] }
type Asset = { id: string; name: string; kind?: string; variants?: Variant[] }
type Reference = { entityId: string; name?: string; kind?: string; variantId?: string; variantVersionId?: string | null }
type Notice = { kind: 'success' | 'error' | 'info'; message: string }
type WebSkill = { id: string; name: string; capability: string }
type WebJob = { jobId: string; state: string; progressPercent: number; errorCode?: string; errorMessage?: string; attempt: number; retryOfJobId?: string; rootJobId: string; canRetry: boolean; outputs: { role: string; ref: string }[] }

function hasId(value: unknown): value is { id: string; [key: string]: unknown } {
  return !!value && typeof value === 'object' && !Array.isArray(value) && typeof (value as { id?: unknown }).id === 'string' && !!(value as { id: string }).id.trim()
}

/** entities.json uses id at every level; names are labels, never identity keys. */
export function mapAssets(value: unknown): { entities: Asset[]; invalidCount: number } {
  if (!value || typeof value !== 'object' || !Array.isArray((value as { entities?: unknown }).entities)) throw new Error('响应格式不正确')
  const raw = (value as { entities: unknown[] }).entities
  const entities = raw.filter((item): item is Asset => hasId(item) && typeof item.name === 'string').map((item) => ({
    id: item.id, name: item.name, kind: typeof item.kind === 'string' ? item.kind : undefined,
    variants: Array.isArray(item.variants) ? item.variants.filter(hasId).map((variant) => ({
      id: variant.id, name: typeof variant.name === 'string' ? variant.name : undefined,
      versions: Array.isArray(variant.versions) ? variant.versions.filter(hasId).map((version) => ({
        id: version.id, number: typeof version.number === 'number' ? version.number : undefined,
        label: typeof version.label === 'string' ? version.label : undefined
      })) : undefined
    })) : undefined
  }))
  return { entities, invalidCount: raw.length - entities.length }
}

export function recordReferences(item: OperationRecord): Reference[] {
  const raw = item.record.references
  if (!Array.isArray(raw)) return []
  return raw.filter((ref): ref is Reference => !!ref && typeof ref === 'object' && typeof ref.entityId === 'string' && !!ref.entityId.trim())
}

export function resolveReference(ref: Reference, assets: Asset[], available = true): { asset?: Asset; variant?: Variant; version?: Version; error?: string; mode: string } {
  const mode = ref.variantVersionId ? '锁定版本' : '跟随最新'
  if (!available) return { mode, error: '资产库不可用，无法核对引用' }
  const asset = assets.find((item) => item.id === ref.entityId)
  if (!asset) return { mode, error: `实体缺失：${ref.entityId}` }
  if (!ref.variantId) return { asset, mode, error: '引用未指定变体 ID' }
  const variant = asset.variants?.find((item) => item.id === ref.variantId)
  if (!variant) return { asset, mode, error: `变体缺失：${ref.variantId}` }
  if (!ref.variantVersionId) return { asset, variant, mode }
  const version = variant.versions?.find((item) => item.id === ref.variantVersionId)
  return version ? { asset, variant, version, mode } : { asset, variant, mode, error: `锁定版本缺失：${ref.variantVersionId}` }
}

function recordTitle(item: OperationRecord): string {
  return typeof item.record.title === 'string' ? item.record.title : typeof item.record.name === 'string' ? item.record.name : item.recordType
}
function recordContent(item: OperationRecord): string {
  return typeof item.record.content === 'string' ? item.record.content : typeof item.record.text === 'string' ? item.record.text : ''
}
function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : '未知错误'
}
function validRecord(value: unknown): value is OperationRecord {
  if (!value || typeof value !== 'object') return false
  const item = value as Partial<OperationRecord>
  return typeof item.recordId === 'string' && typeof item.recordType === 'string' && !!item.record && typeof item.record === 'object' && !Array.isArray(item.record)
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
  const [scene, setScene] = useState<WebScene | null>(null)
  const [assets, setAssets] = useState<Asset[]>([])
  const [assetsReady, setAssetsReady] = useState(false)
  const [selectedId, setSelectedId] = useState('')
  const [assetId, setAssetId] = useState('')
  const [title, setTitle] = useState('')
  const [content, setContent] = useState('')
  const [notice, setNotice] = useState<Notice>({ kind: 'info', message: '登录后加载场景。' })
  const [assetNotice, setAssetNotice] = useState('尚未加载资产。')
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [skills, setSkills] = useState<WebSkill[]>([])
  const [jobs, setJobs] = useState<WebJob[]>([])
  const [prompt, setPrompt] = useState('')
  const [taskNotice, setTaskNotice] = useState('技能与任务尚未加载。')
  const [taskBusy, setTaskBusy] = useState(false)
  const generation = useRef(0)
  const nodeRefs = useRef(new Map<string, HTMLElement>())
  const assetRefs = useRef(new Map<string, HTMLButtonElement>())
  const selected = scene?.records.find((item) => item.recordId === selectedId)
  const dirty = !!selected && (title !== recordTitle(selected) || content !== recordContent(selected))

  function selectRecord(id: string) {
    if (dirty && !window.confirm('当前编辑尚未保存，确定放弃修改并切换节点吗？')) return
    const item = scene?.records.find((record) => record.recordId === id)
    if (!item) return
    setSelectedId(id)
    setTitle(recordTitle(item))
    setContent(recordContent(item))
    setNotice({ kind: 'info', message: `已选择节点 ${id}。` })
    requestAnimationFrame(() => nodeRefs.current.get(id)?.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'center' }))
  }

  async function loadScene() {
    if (dirty && !window.confirm('重新加载将放弃未保存的修改，确定继续吗？')) return
    const run = ++generation.current
    setLoading(true)
    setNotice({ kind: 'info', message: '正在加载场景…' })
    setAssetNotice('正在加载资产…')
    setScene(null)
    setAssets([])
    setAssetsReady(false)
    setSkills([])
    setJobs([])
    setTaskNotice('正在加载技能与任务…')
    setSelectedId('')
    setAssetId('')
    const [sceneResult, assetResult] = await Promise.allSettled([
      request<WebScene>('/api/web/scene'),
      request<unknown>('/api/web/assets')
    ])
    if (run !== generation.current) return
    setLoading(false)
    if (sceneResult.status === 'fulfilled' && Number.isFinite(sceneResult.value?.revision) && Array.isArray(sceneResult.value.records) && sceneResult.value.records.every(validRecord)) {
      setScene(sceneResult.value)
      setNotice({ kind: 'success', message: `场景加载成功，修订 ${sceneResult.value.revision}，共 ${sceneResult.value.records.length} 条记录。` })
    } else {
      setNotice({ kind: 'error', message: `场景加载失败：${sceneResult.status === 'rejected' ? errorMessage(sceneResult.reason) : '响应格式不正确'}` })
    }
    try {
      if (assetResult.status === 'rejected') throw assetResult.reason
      const { entities, invalidCount } = mapAssets(assetResult.value)
      setAssets(entities)
      setAssetsReady(true)
      setAssetNotice(`资产加载成功，共 ${entities.length} 项。${invalidCount ? ` 跳过 ${invalidCount} 项缺少有效 id/name 的实体。` : ''}`)
    } catch (error) {
      setAssetNotice(`资产加载失败：${errorMessage(error)}`)
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

  /** 问一次服务端「我是谁」。没登录就什么都不加载——**别在没身份的时候去打场景接口**。 */
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
    // 只读角色在服务端本来就会被拒（拿到的是 403），但界面不该先给一个点了必然失败的按钮。
    if (auth?.user && !canEdit(auth.user.role)) {
      setNotice({ kind: 'error', message: '你的账号是只读，改不了画布。' })
      return
    }
    const id = selected.recordId
    const revision = scene.revision
    const draftTitle = title
    const draftContent = content
    setSaving(true)
    setNotice({ kind: 'info', message: '正在保存…' })
    try {
      const result = await request<{ revision: number; record: OperationRecord }>(`/api/web/records/${encodeURIComponent(id)}`, {
        method: 'PUT', body: JSON.stringify({ baseRevision: revision, title: draftTitle, content: draftContent })
      })
      if (!Number.isFinite(result?.revision) || !validRecord(result.record) || result.record.recordId !== id) throw new Error('保存响应格式不正确')
      setScene((current) => current ? { revision: result.revision, records: current.records.map((item) => item.recordId === id ? result.record : item) } : current)
      setTitle(recordTitle(result.record))
      setContent(recordContent(result.record))
      setNotice({ kind: 'success', message: `保存成功：${id} · 修订 ${result.revision}。` })
    } catch (error) {
      setNotice({ kind: 'error', message: `保存失败：${errorMessage(error)}。修改仍在编辑框中；修订冲突时请重新加载场景。` })
    } finally {
      setSaving(false)
    }
  }

  function locateAsset(id: string) {
    const asset = assets.find((item) => item.id === id)
    if (!asset) {
      setAssetNotice(`资产定位失败：实体 ID ${id} 在当前项目库中不存在。`)
      return
    }
    setAssetId(id)
    requestAnimationFrame(() => assetRefs.current.get(id)?.scrollIntoView({ behavior: 'smooth', block: 'nearest' }))
    setAssetNotice(`已按实体 ID 定位资产 ${id}。${asset.name ? ` ${asset.name}` : ''}（仅定位，未修改资产）`)
  }

  // 还没问出「我是谁」之前不要画工作台：先显示登录页再跳走会闪一下，也容易让人以为要填两次。
  if (!auth) return <div className="session-panel">
    <div className="session-card"><h2>正在确认登录状态…</h2><p className="session-hint">如果一直停在这里，说明服务端没起来。</p></div>
  </div>

  if (authFailure) return <div className="session-panel">
    <div className="session-card">
      <h2>无法确认登录状态</h2>
      <p className="session-hint">{authFailure}</p>
      <button type="button" onClick={() => void refreshSession()}>重试</button>
    </div>
  </div>

  if (gateOf(auth) !== 'ready') return <SessionPanel state={auth} onChanged={() => void refreshSession()} />

  // 过了上面那道门就一定有用户，取一次给下面用，省得每次都要再判一遍可空。
  const currentUser = auth.user!

  return <div className="workflow-shell web-shell">
    <header className="workflow-header">
      <div className="brand-lockup"><strong>YEEYEEYEE</strong><span>场景画布</span></div>
      <SessionPanel state={auth} onChanged={() => void refreshSession()} />
      <span className="web-revision">修订 {scene?.revision ?? '—'}</span>
    </header>
    <aside className="node-library web-sidebar">
      <strong>场景记录</strong>
      <p className={`web-notice ${notice.kind}`} role="status">{notice.message}</p>
      <label className="web-picker">节点定位
        <select value={selectedId} onChange={(event) => selectRecord(event.target.value)} disabled={!scene?.records.length}>
          <option value="">选择稳定记录 ID</option>
          {scene?.records.map((item) => <option key={item.recordId} value={item.recordId}>{recordTitle(item)} · {item.recordId}</option>)}
        </select>
      </label>
      <section aria-label="本机技能与任务">
        <strong>本机技能与任务</strong>
        <p className="web-notice" role="status">{taskNotice}</p>
        {skills.map((skill) => <div key={skill.id} className="web-reference">
          <strong>{skill.name}</strong><code>{skill.id}</code>
          <label>提示词<textarea value={prompt} onChange={(event) => setPrompt(event.target.value)} maxLength={4000} /></label>
          <button type="button" disabled={!prompt.trim() || taskBusy} onClick={() => void taskAction(`/api/web/skills/${encodeURIComponent(skill.id)}/invoke`, { prompt, idempotencyKey: crypto.randomUUID() })}>调用技能</button>
        </div>)}
        {jobs.map((job) => <div key={job.jobId} className="web-reference">
          <code>{job.jobId}</code><span>第 {job.attempt} 次 · {job.state} · {job.progressPercent}%</span>
          {job.retryOfJobId && <code>重试自 {job.retryOfJobId}</code>}
          {job.errorCode && <b role="alert">{job.errorCode}：{job.errorMessage || '任务失败'}</b>}
          {job.outputs?.map((output, index) => <code key={index}>{output.role}：{output.ref}</code>)}
          <button type="button" disabled={taskBusy || !['Queued', 'Running'].includes(job.state)} onClick={() => void taskAction(`/api/web/jobs/${job.jobId}/cancel`)}>取消</button>
          <button type="button" disabled={taskBusy || !job.canRetry} onClick={() => void taskAction(`/api/web/jobs/${job.jobId}/retry`)}>重试</button>
        </div>)}
      </section>
      <strong>项目资产（只读）</strong>
      <p className="web-notice" role="status">{assetNotice}</p>
      <label className="web-picker">资产定位
        <select value={assetId} onChange={(event) => locateAsset(event.target.value)} disabled={!assets.length}>
          <option value="">选择实体稳定 ID</option>
          {assets.map((asset) => <option key={asset.id} value={asset.id}>{asset.name || '未命名'} · {asset.id}</option>)}
        </select>
      </label>
      <div className="web-assets">
        {assets.map((asset) => <button type="button" key={asset.id} ref={(element) => { if (element) assetRefs.current.set(asset.id, element); else assetRefs.current.delete(asset.id) }} className={assetId === asset.id ? 'active' : ''} onClick={() => locateAsset(asset.id)}>
          <strong>{asset.kind || '资产'} · {asset.name || '未命名'}</strong><code>实体 ID：{asset.id}</code>
          {asset.variants?.map((variant) => <span className="web-asset-variant" key={variant.id}>{variant.name || '未命名变体'} <code>变体 ID：{variant.id}</code>
            {variant.versions?.map((version) => <span className="web-asset-version" key={version.id}>{version.label || (version.number != null ? `v${version.number}` : '版本')} <code>版本 ID：{version.id}</code></span>)}
          </span>)}
        </button>)}
      </div>
    </aside>
    <main className="workflow-canvas web-canvas" aria-label="场景节点">
      {scene?.records.map((item) => <button key={item.recordId} ref={(element) => { if (element) nodeRefs.current.set(item.recordId, element); else nodeRefs.current.delete(item.recordId) }} className={`web-node ${selectedId === item.recordId ? 'selected' : ''}`} onClick={() => selectRecord(item.recordId)}>
        <small>L{layerOf(item.recordType)} · {item.recordType} · {chapterLabelOf(item)}</small>
        <strong>{recordTitle(item)}</strong><span>{recordContent(item) || '暂无内容'}</span><code>{item.recordId}</code>
        {recordReferences(item).map((ref, index) => {
          const resolved = resolveReference(ref, assets, assetsReady)
          return <span className={`web-node-ref ${resolved.error ? 'web-missing' : ''}`} key={`${ref.entityId}-${index}`}>{resolved.asset?.name || ref.name || ref.entityId} · {resolved.mode}{resolved.error ? ` · ${resolved.error}` : ''}</span>
        })}
      </button>)}
      {!scene && <div className="web-empty">加载场景后显示节点。</div>}
      {scene?.records.length === 0 && <div className="web-empty">场景暂无记录。</div>}
    </main>
    <aside className="inspector web-inspector">
      <strong>记录详情</strong>
      {selected ? <>
        <div className="detail-row"><span>记录 ID</span><code>{selected.recordId}</code></div>
        <div className="detail-row"><span>类型</span><b>{selected.recordType}</b></div>
        <label>标题<input value={title} onChange={(event) => { setTitle(event.target.value); setNotice({ kind: 'info', message: '有未保存的修改。' }) }} disabled={saving} /></label>
        <label>内容<textarea value={content} onChange={(event) => { setContent(event.target.value); setNotice({ kind: 'info', message: '有未保存的修改。' }) }} disabled={saving} /></label>
        <button className="web-save" disabled={!dirty || saving || !canEdit(currentUser.role)} onClick={() => void save()}>{saving ? '保存中…' : canEdit(currentUser.role) ? '保存标题和内容' : '只读账号不能保存'}</button>
        <p className="inspector-foot">仅在服务端确认保存成功后更新场景修订；失败时保留编辑内容。资产与引用只读，不会修改项目资产。</p>
        <section className="web-references" aria-label="节点引用"><strong>节点引用</strong>
          {recordReferences(selected).length === 0 && <p className="inspector-foot">暂无引用。</p>}
          {recordReferences(selected).map((ref, index) => {
            const resolved = resolveReference(ref, assets, assetsReady)
            return <div className={`web-reference ${resolved.error ? 'web-missing' : ''}`} key={`${ref.entityId}-${index}`}>
              <div><strong>{resolved.asset?.name || ref.name || '未知实体'}</strong><span className="web-ref-mode">{resolved.mode}</span></div>
              <code>实体 ID：{ref.entityId}</code>
              <code>变体 ID：{ref.variantId || '未指定'}</code>
              {resolved.variant && <span>变体：{resolved.variant.name || '未命名变体'}</span>}
              <code>版本 ID：{ref.variantVersionId || '跟随最新（无锁定 ID）'}</code>
              {resolved.version && <span>版本：{resolved.version.label || (resolved.version.number != null ? `v${resolved.version.number}` : '未命名版本')}</span>}
              {resolved.error && <b role="alert">{resolved.error}</b>}
              <button type="button" className="web-locate" disabled={!resolved.asset} onClick={() => locateAsset(ref.entityId)}>按实体 ID 定位资产</button>
            </div>
          })}
        </section>
      </> : <div className="inspector-empty">选择节点后可编辑标题与内容并查看引用。</div>}
    </aside>
  </div>
}
