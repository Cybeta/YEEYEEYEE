import { useEffect, useState } from 'react'
import { request, errorMessage } from '../api'

type Profile = { id: string; displayName: string; endpoint: string; model: string; apiFormat: number; useFullUrl: boolean; enabled: boolean; hasKey: boolean }
type Models = { revision: string; selectedProfileId?: string; profiles: Profile[] }
const empty = (): Profile => ({ id: crypto.randomUUID(), displayName: '', endpoint: '', model: '', apiFormat: 0, useFullUrl: false, enabled: true, hasKey: false })

export function ModelSettings({ canManage }: { canManage: boolean }) {
  const [models, setModels] = useState<Models | null>(null)
  const [draft, setDraft] = useState<Profile | null>(null)
  const [key, setKey] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  async function reload() { const next = await request<Models>('/api/web/models'); setModels(next); setKey(''); setDraft(null) }
  useEffect(() => { void reload().catch(e => setNotice(errorMessage(e))) }, [])
  async function act(id: string, operation: 'save' | 'select' | 'test' | 'delete' | 'clear-key') {
    if (!models || !canManage) return
    if (operation === 'delete' && !window.confirm('删除这个模型配置？当前模型删除后会切换到其他启用模型。')) return
    if (operation === 'clear-key' && !window.confirm('清除服务端保存的这个模型密钥？')) return
    setBusy(true); setNotice('')
    try {
      const suffix = operation === 'delete' ? `?revision=${encodeURIComponent(models.revision)}` : operation === 'save' ? '' : '/' + operation
      const result = await request<{ message?: string }>(`/api/web/models/${encodeURIComponent(id)}${suffix}`, {
        method: operation === 'delete' ? 'DELETE' : operation === 'save' ? 'PUT' : 'POST',
        body: operation === 'test' || operation === 'delete' ? undefined : JSON.stringify(operation === 'save' ? { ...draft, revision: models.revision, apiKey: key || null } : { revision: models.revision })
      })
      setNotice(result.message ?? (operation === 'save' ? '配置已加密保存' : '当前模型已切换'))
      if (operation !== 'test') await reload()
    } catch (e) { setNotice(errorMessage(e)); setKey('') } finally { setBusy(false) }
  }
  return <div className="df-section">
    <span className="df-heading">聊天模型配置</span>
    <span className="df-dim">全局配置；管理员可修改。密钥仅提交新值，留空保留旧值。</span>
    <button className="df-mini-button" disabled={busy} onClick={() => void reload().catch(e => setNotice(errorMessage(e)))}>重新加载</button>
    {models?.profiles.map(p => <div key={p.id} className="df-card df-section">
      <span>{p.displayName || p.model} {models.selectedProfileId === p.id ? '· 当前' : ''} · {p.hasKey ? '已存密钥' : '未存密钥'}</span>
      <span className="df-dim">{p.endpoint} · {p.model}</span>
      <div className="df-row">
        <button className="df-mini-button" disabled={busy || !canManage} onClick={() => { setDraft({ ...p }); setKey('') }}>编辑</button>
        <button className="df-mini-button" disabled={busy || !canManage} onClick={() => void act(p.id, 'select')}>切换</button>
        <button className="df-mini-button" disabled={busy || !canManage} onClick={() => void act(p.id, 'test')}>测试连接</button>
        <button className="df-mini-button" disabled={busy || !canManage || !p.hasKey} onClick={() => void act(p.id, 'clear-key')}>清除密钥</button>
        <button className="df-mini-button" disabled={busy || !canManage} onClick={() => void act(p.id, 'delete')}>删除</button>
      </div>
    </div>)}
    <button className="df-mini-button" disabled={busy || !canManage || !models} onClick={() => { setDraft(empty()); setKey('') }}>新增模型</button>
    {draft && <div className="df-card df-section">
      {(['displayName', 'endpoint', 'model'] as const).map((field, i) => <label key={field}>{['显示名称', '接口地址', '模型名称'][i]}<input className="df-input" value={draft[field]} disabled={busy} onChange={e => setDraft({ ...draft, [field]: e.target.value })} /></label>)}
      <label>接口格式<select className="df-input" value={draft.apiFormat} disabled={busy} onChange={e => setDraft({ ...draft, apiFormat: Number(e.target.value) })}><option value={0}>OpenAI</option><option value={1}>Anthropic</option></select></label>
      <label><input type="checkbox" checked={draft.useFullUrl} disabled={busy} onChange={e => setDraft({ ...draft, useFullUrl: e.target.checked })} />完整接口 URL</label>
      <label><input type="checkbox" checked={draft.enabled} disabled={busy} onChange={e => setDraft({ ...draft, enabled: e.target.checked })} />启用</label>
      <label>新密钥<input type="password" autoComplete="new-password" className="df-input" value={key} disabled={busy} onChange={e => setKey(e.target.value)} placeholder="留空保留旧密钥" /></label>
      <button className="df-mini-button" disabled={busy || !draft.endpoint || !draft.model} onClick={() => void act(draft.id, 'save')}>保存</button>
      <button className="df-mini-button" disabled={busy} onClick={() => { setDraft(null); setKey('') }}>取消编辑</button>
    </div>}
    {notice && <span role="status" className="df-notice">{notice}</span>}
  </div>
}
