import { useCallback, useEffect, useRef, useState } from 'react'
import { request, errorMessage } from './api'
import { WebCanvasApp } from './WebCanvasApp'

type Canvas = { id: string; title: string }
type Project = { id: string; name: string; canvases: Canvas[] }
type Catalog = { projects: Project[]; defaultCanvasId?: string; canCreate: boolean; canEdit: boolean }

export function ProjectWorkbench() {
  const [catalog, setCatalog] = useState<Catalog>({ projects: [], canCreate: false, canEdit: false })
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const switchBlocked = useRef(false)
  const setSwitchBlocked = useCallback((blocked: boolean) => { switchBlocked.current = blocked }, [])
  const [destination, setDestination] = useState<string | null>(null)
  useEffect(() => {
    if (destination !== null) window.location.assign(destination)
  }, [destination])
  const [name, setName] = useState('')
  const [type, setType] = useState('Other')
  const [path, setPath] = useState('')
  const [canvasTitle, setCanvasTitle] = useState('')
  const selected = new URLSearchParams(window.location.search).get('canvas') ?? catalog.defaultCanvasId ?? ''
  const project = catalog.projects.find(p => p.canvases.some(c => c.id === selected))
  async function refresh() { setCatalog(await request<Catalog>('/api/web/projects')) }
  useEffect(() => {
    void refresh().catch(() => {})
    const timer = window.setInterval(() => void refresh().catch(() => {}), 10000)
    return () => window.clearInterval(timer)
  }, [])
  function maySwitch() {
    if (switchBlocked.current) { setNotice('请先保存节点草稿，并等待当前操作完成。'); return false }
    return true
  }
  function switchCanvas(id: string) {
    if (id === selected || !maySwitch()) return
    setBusy(true); setNotice('')
    const url = new URL(window.location.href)
    if (id) url.searchParams.set('canvas', id)
    else url.searchParams.delete('canvas')
    // Unmount first: stop SSE/chat/lease heartbeats and release only this editor's lease.
    setDestination(url.href)
  }
  async function createProject(open: boolean) {
    if (!maySwitch()) return
    setBusy(true); setNotice('')
    try {
      const next = await request<Project>(open ? '/api/web/projects/open' : '/api/web/projects', {
        method: 'POST', body: JSON.stringify(open ? { path } : { name, type })
      })
      await refresh()
      if (next.canvases[0]) switchCanvas(next.canvases[0].id)
      else setNotice('项目已打开，尚无画布。请在项目列表中创建第一张画布。')
    } catch (e) { setNotice(errorMessage(e)) } finally { setBusy(false) }
  }
  async function createCanvas(projectId: string) {
    if (!maySwitch()) return
    setBusy(true); setNotice('')
    try {
      const next = await request<Canvas>(`/api/web/projects/${projectId}/canvases`, {
        method: 'POST', body: JSON.stringify({ title: canvasTitle.trim() || null })
      })
      switchCanvas(next.id)
    } catch (e) { setNotice(errorMessage(e)) } finally { setBusy(false) }
  }
  return <>
    <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '6px 16px', background: '#171922', color: '#eee', flexWrap: 'wrap' }}>
      <label>项目与画布 <select aria-label="项目与画布" value={selected} disabled={busy} onChange={e => switchCanvas(e.target.value)}>
        <option value="">默认画布</option>
        {catalog.projects.map(p => <optgroup key={p.id} label={p.name}>{p.canvases.map(c => <option key={c.id} value={c.id}>{c.title}</option>)}</optgroup>)}
      </select></label>
      <input aria-label="新画布名称" placeholder="新画布名称（可选）" value={canvasTitle} maxLength={80} onChange={e => setCanvasTitle(e.target.value)} />
      <button disabled={busy || !project || !catalog.canEdit} onClick={() => project && void createCanvas(project.id)}>新增画布</button>
      {catalog.projects.filter(p => p.canvases.length === 0).map(p => <button key={p.id} disabled={busy || !catalog.canEdit} onClick={() => void createCanvas(p.id)}>{p.name}：创建第一张画布</button>)}
      <input aria-label="新项目名称" placeholder="新项目名称" value={name} maxLength={80} onChange={e => setName(e.target.value)} />
      <select aria-label="项目类型" value={type} onChange={e => setType(e.target.value)}>
        <option value="Other">其他</option><option value="Video">视频</option><option value="Image">图像</option><option value="Novel">小说</option>
      </select>
      <button disabled={busy || !catalog.canCreate || !name.trim()} onClick={() => void createProject(false)}>新建项目</button>
      <input aria-label="服务端项目路径" placeholder="项目目录内相对路径或服务端路径" value={path} onChange={e => setPath(e.target.value)} />
      <button disabled={busy || !catalog.canCreate || !path.trim()} onClick={() => void createProject(true)}>打开项目</button>
      <span style={{ fontSize: 12 }}>切换前请保存节点草稿</span>
      {notice && <span role="alert">{notice}</span>}
    </div>
    {destination === null && <WebCanvasApp onSwitchBlocked={setSwitchBlocked} />}
  </>
}
