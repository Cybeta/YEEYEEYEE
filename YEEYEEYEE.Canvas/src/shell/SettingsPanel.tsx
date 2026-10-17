import { useEffect, useState } from 'react'
import { ModelSettings } from './ModelSettings'
import { SkillSettings } from './SkillSettings'
import { errorMessage, request } from '../api'
import { parseSettings, settingsGroups, settingsPages, parseComfyUiProbe, describeComfyUiProbe, type ComfyUiProbe, type SettingsPage, type WebSettings } from './settingsView'

type MediaDraft = {
  revision: string; imageEndpoint: string; imageModel: string; comfyUiBaseUrl: string;
  comfyUiCheckpoint: string; videoEndpoint: string; videoModel: string;
  imageProxyUrl: string; videoProxyUrl: string;
  imageMaxReferenceImages: number; imageHasKey: boolean; videoHasKey: boolean; videoDefaultSeconds: number
}
type VideoImportModel = { alias: string; width: number; height: number; seconds: number; resolution: string; price: number | null; priceFormula: string }
type VideoInspection = { endpoint: string; models: VideoImportModel[]; sourceUrl: string; listSource: string; note: string; message: string }

function MediaSettings({ canManage, onChanged }: { canManage: boolean; onChanged: () => void }) {
  const [draft, setDraft] = useState<MediaDraft | null>(null)
  const [keys, setKeys] = useState({ imageApiKey: '', videoApiKey: '', clearImageKey: false, clearVideoKey: false })
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [videoUrl, setVideoUrl] = useState('')
  const [inspection, setInspection] = useState<VideoInspection | null>(null)
  const [selectedModel, setSelectedModel] = useState('')
  const [reuseImageKeyForVideo, setReuseImageKeyForVideo] = useState(false)
  async function inspectVideo() {
    if (busy || !videoUrl.trim()) return
    setBusy(true)
    setInspection(null)
    setSelectedModel('')
    setNotice('')
    try {
      const result = await request<VideoInspection>('/api/web/settings/media/video/inspect', { method: 'POST', body: JSON.stringify({ url: videoUrl.trim() }) })
      setInspection(result)
      setNotice(result.message)
    } catch (error) { setNotice(errorMessage(error)) }
    finally { setBusy(false) }
  }
  async function reload() {
    setBusy(true)
    try {
      setDraft(await request<MediaDraft>('/api/web/settings/media'))
      setKeys({ imageApiKey: '', videoApiKey: '', clearImageKey: false, clearVideoKey: false })
      setReuseImageKeyForVideo(false)
      setInspection(null)
      setSelectedModel('')
      setNotice('')
    } catch (error) { setNotice(errorMessage(error)) }
    finally { setBusy(false) }
  }
  useEffect(() => { if (canManage) void reload() }, [canManage])
  if (!canManage) return null
  const fields = [
    ['imageEndpoint', '图像接口地址'], ['imageModel', '图像模型'],
    ['comfyUiBaseUrl', 'ComfyUI 地址'], ['comfyUiCheckpoint', 'ComfyUI checkpoint'],
    ['videoEndpoint', '视频接口地址'], ['videoModel', '视频模型']
  ] as const
  async function save() {
    if (!draft || busy) return
    if (!Number.isInteger(draft.imageMaxReferenceImages) || draft.imageMaxReferenceImages < 0 || draft.imageMaxReferenceImages > 32) {
      setNotice('最大参考图数量必须为 0–32 的整数。')
      return
    }
    if (!Number.isInteger(draft.videoDefaultSeconds) || draft.videoDefaultSeconds < 0 || draft.videoDefaultSeconds > 3600) {
      setNotice('视频默认秒数必须为 0–3600 的整数。')
      return
    }
    setBusy(true)
    try {
      await request('/api/web/settings/media', { method: 'PUT', body: JSON.stringify({ ...draft, ...keys, reuseImageKeyForVideo }) })
      await reload()
      onChanged()
      setNotice('已保存到服务端独立配置，图片与 HTTP 视频 API 设置将在下一次任务生效。ComfyUI 仍使用服务启动配置。')
    } catch (error) { setNotice(errorMessage(error)) }
    finally { setBusy(false) }
  }
  return <div className="df-section">
    <span className="df-heading">媒体设置</span>
    <span className="df-notice">编辑服务端独立媒体配置；密钥只保存在服务端，不会回传到桌面端。</span>
    {draft && <>
      <label className="df-field">视频服务文档 URL
        <input className="df-input" value={videoUrl} disabled={busy} maxLength={2048} placeholder="https://video.anyaiapi.top/about"
          onChange={event => { setVideoUrl(event.target.value); setInspection(null); setSelectedModel('') }} />
        <span className="df-dim">只读文档与模型目录，使用已保存的视频代理；选择模型只填草稿，不会生成视频。</span>
      </label>
      <button className="df-mini-button" disabled={busy || !videoUrl.trim()} onClick={() => void inspectVideo()}>读取视频模型</button>
      {inspection && <>
        <span className="df-dim">识别接口：{inspection.endpoint}；目录：{inspection.listSource}</span>
        {inspection.note && <span className="df-notice">{inspection.note}</span>}
        <label className="df-field">选择启用视频模型（最低已知价格档位）
          <select className="df-input" value={selectedModel} disabled={busy} onChange={event => {
            setSelectedModel(event.target.value)
            const model = inspection.models[Number(event.target.value)]
            if (event.target.value !== '' && model) {
              setDraft({ ...draft, videoEndpoint: inspection.endpoint, videoModel: model.alias, videoDefaultSeconds: model.seconds })
              setNotice('已填入视频接口、完整模型名和默认秒数草稿；请点击保存媒体设置。')
            }
          }}>
            <option value="">请选择模型</option>
            {inspection.models.map((model, index) => <option key={`${model.alias}-${index}`} value={index}>{model.alias} · {model.seconds || '默认'}秒 · {model.resolution || '默认分辨率'} · {model.price === null ? '价格未知' : `${model.price} 积分`}</option>)}
          </select>
        </label>
        {selectedModel !== '' && inspection.models[Number(selectedModel)] && (() => {
          const model = inspection.models[Number(selectedModel)]
          return <span className="df-dim">参数：{model.width} × {model.height}，{model.seconds || '服务端默认'}秒；{model.price === null ? '无法报价' : `${model.price} 积分`}（{model.priceFormula}）。报价来自目录，未提交生成请求。</span>
        })()}
      </>}
      {fields.map(([field, label]) => <label key={field} className="df-field">{label}
        <input className="df-input" value={draft[field]} disabled={busy} maxLength={field.endsWith('Endpoint') || field === 'comfyUiBaseUrl' ? 2048 : 512}
          onChange={event => setDraft({ ...draft, [field]: event.target.value })} />
      </label>)}
      {(['imageProxyUrl', 'videoProxyUrl'] as const).map(field => <label key={field} className="df-field">{field === 'imageProxyUrl' ? '图像 API 网络代理' : '视频 API 网络代理'}
        <input className="df-input" value={draft[field]} disabled={busy} maxLength={2048} placeholder="http://127.0.0.1:10809"
          onChange={event => setDraft({ ...draft, [field]: event.target.value })} />
        <span className="df-dim">填写 HTTP、HTTPS 或 SOCKS5 完整代理 URL；留空沿用服务端系统代理。127.0.0.1 指服务端机器。仅影响此 API 的提交、轮询与下载，不影响 ComfyUI。</span>
      </label>)}
      <label className="df-field">最大参考图数量
        <input className="df-input" type="number" min={0} max={32} step={1} value={draft.imageMaxReferenceImages} disabled={busy}
          onChange={event => setDraft({ ...draft, imageMaxReferenceImages: event.target.valueAsNumber })} />
        <span className="df-dim">0 表示不支持参考图。</span>
      </label>
      <label className="df-field">视频默认时长（秒）
        <input className="df-input" type="number" min={0} max={3600} step={1} value={draft.videoDefaultSeconds} disabled={busy}
          onChange={event => setDraft({ ...draft, videoDefaultSeconds: event.target.valueAsNumber })} />
        <span className="df-dim">0 表示服务端默认；选择目录模型会填入最低价档位秒数。</span>
      </label>
      <label><input type="checkbox" checked={reuseImageKeyForVideo} disabled={busy || !draft.imageHasKey || keys.clearImageKey}
        onChange={event => { setReuseImageKeyForVideo(event.target.checked); setKeys({ ...keys, videoApiKey: '', clearVideoKey: false }) }} />复用同源、已保存的图片密钥作为视频密钥</label>
      <span className="df-dim">保存时由服务端核对已保存图片接口与视频接口的协议、主机和端口，并复制加密密钥；新填写的图片密钥不参与本次复用。</span>
      {(['image', 'video'] as const).map(kind => {
        const field = kind === 'image' ? 'imageApiKey' : 'videoApiKey'
        const clear = kind === 'image' ? 'clearImageKey' : 'clearVideoKey'
        return <div className="df-section" key={kind}>
          <label className="df-field">{kind === 'image' ? '图像' : '视频'}密钥（{draft[kind === 'image' ? 'imageHasKey' : 'videoHasKey'] ? '已配置' : '未配置'}；留空保留）
            <input className="df-input" type="password" autoComplete="new-password" value={keys[field]} disabled={busy || keys[clear] || kind === 'video' && reuseImageKeyForVideo}
              onChange={event => setKeys({ ...keys, [field]: event.target.value })} />
          </label>
          <label><input type="checkbox" checked={keys[clear]} disabled={busy || reuseImageKeyForVideo}
            onChange={event => setKeys({ ...keys, [clear]: event.target.checked, [field]: '' })} />清除此密钥</label>
        </div>
      })}
      <button className="df-mini-button" disabled={busy} onClick={() => void save()}>保存媒体设置</button>
    </>}
    <button className="df-mini-button" disabled={busy} onClick={() => void reload()}>重新加载媒体配置</button>
    {notice && <span className="df-notice" role="status">{notice}</span>}
  </div>
}

export function SettingsPanel({ canManage = false, onSkillsChanged }: { canManage?: boolean; onSkillsChanged?: () => Promise<void> }) {
  const [page, setPage] = useState<SettingsPage>('models')
  const [settings, setSettings] = useState<WebSettings | null>(null)
  const [error, setError] = useState('')
  const [probe, setProbe] = useState<ComfyUiProbe | null>(null)
  const [probeError, setProbeError] = useState('')
  const [probing, setProbing] = useState(false)

  async function refreshSettings() {
    try {
      setSettings(parseSettings(await request<unknown>('/api/web/settings')))
      setError('')
    } catch (failure) { setError(errorMessage(failure)) }
  }

  useEffect(() => {
    let active = true
    void (async () => {
      try {
        const value = parseSettings(await request<unknown>('/api/web/settings'))
        if (active) {
          setSettings(value)
          setError('')
        }
      } catch (failure) {
        if (active) setError(errorMessage(failure))
      }
    })()
    return () => { active = false }
  }, [])

  /**
   * 按一下才探。**不做成进页面就自动探**：探针要等后端回应（最多几秒），
   * 而这一页绝大多数时候只是被打开看一眼「配了什么」。
   */
  async function testComfyUi() {
    setProbing(true)
    try {
      setProbe(parseComfyUiProbe(await request<unknown>('/api/web/settings/comfyui')))
      setProbeError('')
    } catch (failure) {
      setProbe(null)
      setProbeError(errorMessage(failure))
    } finally {
      setProbing(false)
    }
  }

  return (
    <div className="df-section" style={{ gap: 14 }}>
      <div className="df-row" role="tablist" aria-label="设置分页">
        {settingsPages.map(item => <button key={item.id} type="button" id={`settings-${item.id}-tab`} role="tab" aria-selected={page === item.id} aria-controls={`settings-${item.id}`} className="df-mini-button" style={page === item.id ? { borderColor: 'var(--df-primary)', color: 'var(--df-primary)' } : undefined} onClick={() => setPage(item.id)}>{item.title}</button>)}
      </div>
      <div id="settings-models" role="tabpanel" aria-labelledby="settings-models-tab" hidden={page !== 'models'}>
        <ModelSettings canManage={canManage} onChanged={() => void refreshSettings()} />
      </div>
      <div id="settings-media" role="tabpanel" aria-labelledby="settings-media-tab" hidden={page !== 'media'}>
        <div className="df-section" style={{ gap: 14 }}>
      <MediaSettings canManage={canManage} onChanged={() => void refreshSettings()} />
      {/* 探针使用服务启动配置中的 ComfyUI 地址。 */}
      <div className="df-section" style={{ gap: 6 }}>
        <span className="df-heading" style={{ fontSize: 12 }}>ComfyUI 连接（这台服务端）</span>
        <div className="df-card df-section" style={{ gap: 6 }}>
          <span className="df-dim" style={{ fontSize: 11 }}>
            探一次后端现在通不通。端口连得上不代表对面还是 ComfyUI——云上隧道重启一次地址就变，
            那时它回的常常是机房的一页 404。
          </span>
          <button
            type="button"
            className="df-mini-button"
            style={{ alignSelf: 'flex-start' }}
            disabled={probing}
            onClick={() => void testComfyUi()}
          >{probing ? '正在测试…' : '测试连接'}</button>
          {probe && (
            <span
              className={probe.reachable ? 'df-dim' : 'df-notice is-error'}
              style={{ fontSize: 11, overflowWrap: 'anywhere' }}
            >{describeComfyUiProbe(probe)}</span>
          )}
          {probeError && <span className="df-notice is-error" style={{ fontSize: 11 }}>测试失败：{probeError}</span>}
        </div>
      </div>
        </div>
      </div>
      <div id="settings-skills" role="tabpanel" aria-labelledby="settings-skills-tab" hidden={page !== 'skills'}>
        <SkillSettings canManage={canManage} onChanged={onSkillsChanged} />
        {!canManage && <div className="df-notice">技能与工作流由管理员管理。</div>}
      </div>
      <div id="settings-collaboration" role="tabpanel" aria-labelledby="settings-collaboration-tab" hidden={page !== 'collaboration'}>
        <div className="df-notice">Web 使用当前登录账号；项目协作和编辑状态在工作区查看。</div>
      </div>
      <div className="df-notice">下方为服务端配置摘要；模型或媒体保存后会自动刷新。</div>
      {error || !settings ? <div className={error ? 'df-notice is-error' : 'df-notice'}>{error ? `读不到设置：${error}` : '正在读取设置…'}</div> : settingsGroups(settings).filter(group => group.title === settingsPages.find(item => item.id === page)?.title).map((group) => (
        <div key={group.title} className="df-section" style={{ gap: 6 }}>
          <span className="df-heading" style={{ fontSize: 12 }}>{group.title}</span>
          <div className="df-card">
            {group.rows.map((row) => (
              <div key={row.label} className="df-row-between" style={{ padding: '3px 0' }}>
                <span className="df-dim" style={{ fontSize: 11 }}>{row.label}</span>
                <span
                  style={{
                    fontSize: 11,
                    textAlign: 'right',
                    overflowWrap: 'anywhere',
                    color: row.warn ? 'var(--df-warning)' : 'var(--df-ink-2)'
                  }}
                >
                  {row.value}
                </span>
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  )
}
