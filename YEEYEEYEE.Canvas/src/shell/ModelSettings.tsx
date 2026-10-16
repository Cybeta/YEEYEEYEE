import { useEffect, useState } from 'react'
import { request, errorMessage } from '../api'

type Profile = { id: string; displayName: string; endpoint: string; model: string; apiFormat: number; useFullUrl: boolean; enabled: boolean; hasKey: boolean; presetId: string; contextWindow: number; maxOutputTokens: number; sendSamplingParameters: boolean; supportsImageInput: boolean }
type PresetValues = { endpoint: string; format: number; useFullUrl: boolean; sendsSamplingParameters: boolean; modelId: string; contextWindow: number; maxOutputTokens: number; supportsVision: boolean }
type Preset = { id: string; name: string; hint: string; manualCapabilities: boolean; defaults: PresetValues; models: { id: string; note: string; values: PresetValues }[] }
type Models = { revision: string; selectedProfileId?: string; profiles: Profile[] }
const empty = (): Profile => ({ id: crypto.randomUUID(), displayName: '', endpoint: '', model: '', apiFormat: 0, useFullUrl: false, enabled: true, hasKey: false, presetId: 'custom', contextWindow: 0, maxOutputTokens: 0, sendSamplingParameters: true, supportsImageInput: false })

export function applyModelPreset(draft: Profile, preset: Preset, values = preset.defaults): Profile {
  if (preset.id === 'custom') return { ...draft, presetId: preset.id }
  return { ...draft, presetId: preset.id, displayName: draft.displayName || preset.name, endpoint: values.endpoint, apiFormat: values.format,
    useFullUrl: values.useFullUrl, model: values.modelId, sendSamplingParameters: values.sendsSamplingParameters,
    ...(preset.manualCapabilities ? {} : { contextWindow: values.contextWindow, maxOutputTokens: values.maxOutputTokens, supportsImageInput: values.supportsVision }) }
}

export function ModelSettings({ canManage, onChanged }: { canManage: boolean; onChanged?: () => void }) {
  const [models, setModels] = useState<Models | null>(null)
  const [draft, setDraft] = useState<Profile | null>(null)
  const [presets, setPresets] = useState<Preset[]>([])
  const [presetsLoading, setPresetsLoading] = useState(true)
  const [presetsError, setPresetsError] = useState('')
  const preset = presets.find(p => p.id === draft?.presetId)
  const [key, setKey] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  async function reload() { const next = await request<Models>('/api/web/models'); setModels(next); setKey(''); setDraft(null); onChanged?.() }
  async function reloadPresets() {
    setPresetsLoading(true); setPresetsError('')
    try {
      const result = await request<{ presets: Preset[] }>('/api/web/models/presets')
      setPresets(result.presets)
      if (result.presets.length === 0) setPresetsError('服务端未提供服务商预设')
    } catch (e) { setPresetsError(errorMessage(e)) }
    finally { setPresetsLoading(false) }
  }
  useEffect(() => {
    void reload().catch(e => setNotice(errorMessage(e)))
    void reloadPresets()
  }, [])
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
    <span className="df-dim">服务端独立配置；管理员可修改。密钥仅提交新值，留空保留旧值，不会导入桌面端配置。</span>
    <button className="df-mini-button" disabled={busy || presetsLoading} onClick={() => { void reload().catch(e => setNotice(errorMessage(e))); void reloadPresets() }}>重新加载</button>
    {presetsLoading && <span className="df-notice" role="status">正在读取服务商预设…</span>}
    {presetsError && <div className="df-section">
      <span className="df-notice is-error" role="status">服务商预设加载失败：{presetsError}</span>
      <button className="df-mini-button" disabled={presetsLoading} onClick={() => void reloadPresets()}>重试加载服务商预设</button>
    </div>}
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
      <label>服务商预设<select className="df-input" value={draft.presetId} disabled={busy || presets.length === 0} onChange={e => {
        const chosen = presets.find(p => p.id === e.target.value)
        if (chosen) setDraft(applyModelPreset(draft, chosen))
      }}><option value="custom">自定义（自选接口格式）</option>{presets.filter(p => p.id !== 'custom').map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
      {preset?.hint && <span className="df-dim">{preset.hint}</span>}
      {!!preset?.models.length && <label>模型预设<select className="df-input" value={preset.models.some(m => m.id === draft.model) ? draft.model : ''} disabled={busy} onChange={e => {
        const chosen = preset.models.find(m => m.id === e.target.value)
        if (chosen) setDraft({ ...draft, model: chosen.id, contextWindow: chosen.values.contextWindow, maxOutputTokens: chosen.values.maxOutputTokens, supportsImageInput: chosen.values.supportsVision })
      }}><option value="">手填模型</option>{preset.models.map(m => <option key={m.id} value={m.id}>{m.id}{m.note ? ` — ${m.note}` : ''}</option>)}</select></label>}
      {(['displayName', 'endpoint', 'model'] as const).map((field, i) => <label key={field}>{['显示名称', '接口地址', '模型名称'][i]}<input className="df-input" value={draft[field]} disabled={busy} onChange={e => setDraft({ ...draft, [field]: e.target.value })} /></label>)}
      <label>接口格式<select className="df-input" value={draft.apiFormat} disabled={busy} onChange={e => setDraft({ ...draft, apiFormat: Number(e.target.value) })}><option value={0}>OpenAI</option><option value={1}>Anthropic</option></select></label>
      <label><input type="checkbox" checked={draft.useFullUrl} disabled={busy} onChange={e => setDraft({ ...draft, useFullUrl: e.target.checked })} />完整接口 URL</label>
      <span className="df-dim">能力默认值来自 Desktop 共享预设；0 表示不声明，可按接口实际能力调整。</span>
      {(['contextWindow', 'maxOutputTokens'] as const).map((field, i) => <label key={field}>{['上下文窗口（token）', '单次最大输出（token）'][i]}<input type="number" min={0} step={1} className="df-input" value={draft[field]} disabled={busy} onChange={e => setDraft({ ...draft, [field]: Math.max(0, Math.trunc(Number(e.target.value))) })} /></label>)}
      <label><input type="checkbox" checked={draft.sendSamplingParameters} disabled={busy} onChange={e => setDraft({ ...draft, sendSamplingParameters: e.target.checked })} />发送采样参数</label>
      <label><input type="checkbox" checked={draft.supportsImageInput} disabled={busy} onChange={e => setDraft({ ...draft, supportsImageInput: e.target.checked })} />支持图片输入</label>
      <label><input type="checkbox" checked={draft.enabled} disabled={busy} onChange={e => setDraft({ ...draft, enabled: e.target.checked })} />启用</label>
      <label>新密钥<input type="password" autoComplete="new-password" className="df-input" value={key} disabled={busy} onChange={e => setKey(e.target.value)} placeholder="留空保留旧密钥" /></label>
      <button className="df-mini-button" disabled={busy || !draft.endpoint || !draft.model} onClick={() => void act(draft.id, 'save')}>保存</button>
      <button className="df-mini-button" disabled={busy} onClick={() => { setDraft(null); setKey('') }}>取消编辑</button>
    </div>}
    {notice && <span role="status" className="df-notice">{notice}</span>}
  </div>
}
