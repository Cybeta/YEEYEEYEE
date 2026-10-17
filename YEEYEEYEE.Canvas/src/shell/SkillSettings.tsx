import { useEffect, useRef, useState } from 'react'
import { errorMessage, request } from '../api'
import { startWorkflowConversion, type WorkflowImportResult } from './workflowConversion'

type Skill = { id: string; name: string; enabled: boolean; description: string }
type Workflow = { key: string; title: string; enabled: boolean; kind: string; converted: boolean; error: string; lastImportError: string }
type Site = { id: string; displayName: string; workflows: Workflow[] }
export function SkillSettings({ canManage, onChanged }: { canManage: boolean; onChanged?: () => Promise<void> }) {
  const [baseUrl, setBaseUrl] = useState('')
  const [mediaLoaded, setMediaLoaded] = useState(false)
  const [listing, setListing] = useState<{ baseUrl: string; conversionConfigured: boolean; conversionOrigin: string | null } | null>(null)
  const [results, setResults] = useState<WorkflowImportResult | null>(null)
  const conversion = useRef<ReturnType<typeof startWorkflowConversion> | null>(null)
  const running = useRef(false)
  const mounted = useRef(false)
  const [skills, setSkills] = useState<Skill[]>([])
  const [sites, setSites] = useState<Site[]>([])
  async function loadWorkflows() {
    const result = await request<{ sites: Site[] }>('/api/web/settings/workflows/installed')
    setSites(result.sites)
  }
  async function manage(site: Site, workflow: Workflow, operation: string, name?: string) {
    await request(`/api/web/settings/workflows/${encodeURIComponent(site.id)}/manage`, { method: 'POST', body: JSON.stringify({ key: workflow.key, operation, name, enabled: !workflow.enabled }) })
    await loadWorkflows()
  }
  const [paths, setPaths] = useState<string[]>([])
  const [selected, setSelected] = useState<string[]>([])
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  async function scan() {
    const result = await request<{ skills: Skill[]; errors: string[] }>('/api/web/settings/skills')
    setSkills(result.skills); setNotice(result.errors.join('；'))
    await loadWorkflows()
  }
  useEffect(() => {
    mounted.current = true
    if (canManage) {
      void scan().catch(error => { if (mounted.current) setNotice(errorMessage(error)) })
      void request<{ comfyUiBaseUrl: string }>('/api/web/settings/media').then(media => {
        if (mounted.current) setBaseUrl(current => current || media.comfyUiBaseUrl)
      }).catch(error => { if (mounted.current) setNotice(errorMessage(error)) })
        .finally(() => { if (mounted.current) setMediaLoaded(true) })
    }
    return () => { mounted.current = false; conversion.current?.cancel(); conversion.current = null }
  }, [canManage])
  async function run(action: () => Promise<void>) {
    if (running.current) return
    running.current = true
    setBusy(true)
    try { await action() } catch (error) { if (mounted.current) setNotice(errorMessage(error)) }
    finally { running.current = false; if (mounted.current) setBusy(false) }
  }
  async function imported(result: WorkflowImportResult) {
    if (!mounted.current) return
    setResults(result)
    setNotice(`导入成功 ${result.converted} 份，失败 ${result.failed} 份。`)
    await Promise.all([loadWorkflows(), result.converted > 0 ? onChanged?.() : undefined])
  }
  function officialImport() {
    if (running.current || !listing) return
    void run(async () => {
      setResults(null)
      const current = startWorkflowConversion(selected, listing.baseUrl)
      conversion.current = current
      try { await imported(await current.result) }
      finally { if (conversion.current === current) conversion.current = null }
    })
  }
  async function mutate(skill: Skill, operation: string, name?: string) {
    await request(`/api/web/settings/skills/${encodeURIComponent(skill.id)}`, { method: 'POST', body: JSON.stringify({ operation, name, enabled: !skill.enabled }) })
    await scan()
  }
  if (!canManage) return null
  return <div className="df-section">
    <span className="df-heading">技能与工作流</span>
    <button className="df-mini-button" disabled={busy} onClick={() => void run(scan)}>重新扫描技能</button>
    {skills.map(skill => <div className="df-card" key={skill.id}>
      <input key={skill.name} className="df-input" aria-label="技能名称" defaultValue={skill.name} disabled={busy} onBlur={event => { if (event.target.value !== skill.name) void run(() => mutate(skill, 'rename', event.target.value)) }} />
      <span className="df-muted">{skill.description}</span>
      <button className="df-mini-button" disabled={busy} onClick={() => void run(() => mutate(skill, 'enabled'))}>{skill.enabled ? '停用' : '启用'}</button>
      <button className="df-mini-button" disabled={busy} onClick={() => { if (window.confirm(`删除技能“${skill.name}”？`)) void run(() => mutate(skill, 'delete')) }}>删除</button>
    </div>)}
    <span className="df-muted">导入到桌面共享工作流库。UI 格式使用独立窗口中的官方前端转换；API 格式由服务端直接导入。</span>
    <label className="df-field">ComfyUI 地址
      <input className="df-input" value={baseUrl} disabled={busy || !mediaLoaded} maxLength={2048} placeholder="默认使用媒体配置地址" onChange={event => {
        setBaseUrl(event.target.value); setListing(null); setPaths([]); setSelected([]); setResults(null)
      }} />
    </label>
    <button className="df-mini-button" disabled={busy || !mediaLoaded} onClick={() => void run(async () => {
      const snapshot = baseUrl.trim()
      setListing(null); setPaths([]); setSelected([]); setResults(null)
      const result = await request<{ paths: string[]; conversionConfigured: boolean; conversionOrigin: string | null }>(`/api/web/settings/workflows?baseUrl=${encodeURIComponent(snapshot)}`)
      setPaths(result.paths); setSelected(result.paths); setListing({ baseUrl: snapshot, conversionConfigured: result.conversionConfigured, conversionOrigin: result.conversionOrigin })
    })}>读取 ComfyUI 工作流清单</button>
    {listing && <span className="df-muted">清单地址：{listing.baseUrl || '服务端默认地址'}。{listing.conversionConfigured && listing.conversionOrigin ? `官方转换来源：${listing.conversionOrigin}` : '未配置官方转换来源，可使用“仅导入 API 格式”；UI 格式需配置官方转换来源。'}</span>}
    {paths.map(path => <label key={path}><input type="checkbox" checked={selected.includes(path)} disabled={busy} onChange={event => setSelected(ids => event.target.checked ? [...ids, path] : ids.filter(id => id !== path))} />{path}</label>)}
    <button className="df-mini-button" disabled={busy || selected.length === 0 || !listing?.conversionConfigured || !listing.conversionOrigin} onClick={officialImport}>通过官方前端导入所选工作流</button>
    {busy && conversion.current && <button className="df-mini-button" onClick={() => conversion.current?.cancel()}>取消转换</button>}
    <button className="df-mini-button" disabled={busy || selected.length === 0 || !listing} onClick={() => void run(async () => {
      if (!listing) return
      setResults(null)
      await imported(await request<WorkflowImportResult>('/api/web/settings/workflows/import', { method: 'POST', body: JSON.stringify({ paths: selected, baseUrl: listing.baseUrl }) }))
    })}>仅导入 API 格式</button>
    {results?.workflows.map((workflow, index) => <div className="df-card" key={`${workflow.key}-${index}`}>
      <span>{workflow.title || workflow.key} · {workflow.converted ? '导入成功' : '导入失败'}</span>
      {workflow.note && <span className="df-muted">{workflow.note}</span>}
      {workflow.error && <span className="df-notice is-error">{workflow.error}</span>}
    </div>)}
    {sites.map(site => <div key={site.id}>
      <span className="df-heading">{site.displayName} · 已安装工作流</span>
      {site.workflows.map(workflow => <div className="df-card" key={workflow.key}>
        <input key={workflow.title} className="df-input" aria-label="工作流名称" defaultValue={workflow.title} disabled={busy} onBlur={event => { if (event.target.value !== workflow.title) void run(() => manage(site, workflow, 'rename', event.target.value)) }} />
        <span className="df-muted">{workflow.kind} · {workflow.converted ? 'API 格式已安装' : '导入失败'}{workflow.lastImportError || workflow.error}</span>
        <button className="df-mini-button" disabled={busy || !workflow.converted} onClick={() => void run(() => manage(site, workflow, 'enabled'))}>{workflow.enabled ? '停用' : '启用'}</button>
        <button className="df-mini-button" disabled={busy} onClick={() => { if (window.confirm(`删除工作流“${workflow.title}”？`)) void run(() => manage(site, workflow, 'delete')) }}>删除</button>
      </div>)}
    </div>)}
    {notice && <span role="status" className="df-notice">{notice}</span>}
  </div>
}
