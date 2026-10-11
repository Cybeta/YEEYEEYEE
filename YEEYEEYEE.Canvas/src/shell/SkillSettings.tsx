import { useEffect, useState } from 'react'
import { errorMessage, request } from '../api'

type Skill = { id: string; name: string; enabled: boolean; description: string }
type Workflow = { key: string; title: string; enabled: boolean; kind: string; converted: boolean; error: string; lastImportError: string }
type Site = { id: string; displayName: string; workflows: Workflow[] }
export function SkillSettings({ canManage }: { canManage: boolean }) {
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
  useEffect(() => { if (canManage) void scan().catch(error => setNotice(errorMessage(error))) }, [canManage])
  async function run(action: () => Promise<void>) {
    setBusy(true)
    try { await action() } catch (error) { setNotice(errorMessage(error)) }
    finally { setBusy(false) }
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
    <span className="df-muted">从服务端配置的 ComfyUI 导入到桌面共享工作流库。UI 格式需要官方前端导出器；当前 Web 服务只可直接处理 API 格式，失败会逐项报告。</span>
    <button className="df-mini-button" disabled={busy} onClick={() => void run(async () => {
      const result = await request<{ paths: string[] }>('/api/web/settings/workflows'); setPaths(result.paths); setSelected(result.paths)
    })}>读取 ComfyUI 工作流清单</button>
    {paths.map(path => <label key={path}><input type="checkbox" checked={selected.includes(path)} disabled={busy} onChange={event => setSelected(ids => event.target.checked ? [...ids, path] : ids.filter(id => id !== path))} />{path}</label>)}
    <button className="df-mini-button" disabled={busy || selected.length === 0} onClick={() => void run(async () => {
      const result = await request<{ converted: number; failed: number; workflows: { title: string; converted: boolean; note: string; error: string }[] }>('/api/web/settings/workflows/import', { method: 'POST', body: JSON.stringify({ paths: selected }) })
      setNotice(`导入成功 ${result.converted} 份，失败 ${result.failed} 份。${result.workflows.filter(w => !w.converted).map(w => `${w.title}：${w.error || w.note}`).join('；')}`)
      await loadWorkflows()
    })}>导入所选工作流</button>
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
