import { useEffect, useRef, useState } from 'react'
import { canvasUrl, errorMessage, request } from '../api'

type Film = { revision: number; note: string; missingTitles: string[]; shots: { index: number; nodeId: string; title: string }[]; versions: { index: number; label: string; url: string; factsText: string; shots: string[] }[]; latestChanges: string[] }

export function FilmPanel({ nodeId, canEdit }: { nodeId: string; canEdit: boolean }) {
  const [film, setFilm] = useState<Film | null>(null)
  const [selected, setSelected] = useState<string[]>([])
  const [hint, setHint] = useState('')
  const [busy, setBusy] = useState(false)
  const generation = useRef(0)
  useEffect(() => {
    generation.current++
    let active = true
    setFilm(null); setHint(''); setBusy(false); setSelected([])
    request<Film>(`/api/web/records/${nodeId}/film`).then(value => {
      if (active) { setFilm(value); setSelected(value.shots.map(s => s.nodeId)) }
    }).catch(error => { if (active) setHint(errorMessage(error)) })
    return () => { active = false; generation.current++ }
  }, [nodeId])
  async function concat() {
    if (!film) return
    const current = generation.current
    setBusy(true); setHint('正在拼接…')
    try {
      await request(`/api/web/records/${nodeId}/film`, { method: 'POST', body: JSON.stringify({ baseRevision: film.revision, shotIds: selected }) })
      if (current !== generation.current) return
      const value = await request<Film>(`/api/web/records/${nodeId}/film`)
      if (current === generation.current) { setFilm(value); setHint('成片已保存') }
    } catch (error) { if (current === generation.current) setHint(errorMessage(error)) }
    finally { if (current === generation.current) setBusy(false) }
  }
  return <div className="df-section">
    <span className="df-label">成片拼接与版本</span>
    {film && <>
      <span className="df-muted">{film.note}</span>
      {film.shots.map(shot => <label key={shot.nodeId}><input type="checkbox" disabled={!canEdit || busy} checked={selected.includes(shot.nodeId)} onChange={event => setSelected(ids => event.target.checked ? [...ids, shot.nodeId] : ids.filter(id => id !== shot.nodeId))} />第 {shot.index} 镜 · {shot.title}</label>)}
      {film.missingTitles.map(title => <span className="df-muted" key={title}>缺视频：{title}</span>)}
      <button className="df-mini-button" disabled={!canEdit || busy || selected.length < 2} onClick={() => void concat()}>按镜头顺序拼接所选视频</button>
      {film.versions.map(version => <div className="df-card" key={version.index}><a href={canvasUrl(version.url)} download>{version.label} · 下载 MP4</a><div className="df-muted">{version.factsText}</div><div className="df-muted">{version.shots.join('、')}</div></div>)}
      {film.latestChanges.map(change => <span className="df-muted" key={change}>{change}</span>)}
    </>}
    {hint && <span role="status" className="df-muted">{hint}</span>}
  </div>
}
