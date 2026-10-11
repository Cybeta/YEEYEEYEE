import { useEffect, useState } from 'react'
import { ModelSettings } from './ModelSettings'
import { SkillSettings } from './SkillSettings'
import { errorMessage, request } from '../api'
import { parseSettings, settingsGroups, parseComfyUiProbe, describeComfyUiProbe, type ComfyUiProbe, type WebSettings } from './settingsView'

type MediaDraft = {
  revision: string; imageEndpoint: string; imageModel: string; comfyUiBaseUrl: string;
  comfyUiCheckpoint: string; videoEndpoint: string; videoModel: string;
  imageHasKey: boolean; videoHasKey: boolean
}

function MediaSettings({ canManage }: { canManage: boolean }) {
  const [draft, setDraft] = useState<MediaDraft | null>(null)
  const [keys, setKeys] = useState({ imageApiKey: '', videoApiKey: '', clearImageKey: false, clearVideoKey: false })
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  async function reload() {
    setBusy(true)
    try {
      setDraft(await request<MediaDraft>('/api/web/settings/media'))
      setKeys({ imageApiKey: '', videoApiKey: '', clearImageKey: false, clearVideoKey: false })
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
    setBusy(true)
    try {
      await request('/api/web/settings/media', { method: 'PUT', body: JSON.stringify({ ...draft, ...keys }) })
      await reload()
      setNotice('已保存到桌面共享配置。Web 任务服务仍使用服务启动配置中的地址。')
    } catch (error) { setNotice(errorMessage(error)) }
    finally { setBusy(false) }
  }
  return <div className="df-section">
    <span className="df-heading">媒体设置</span>
    <span className="df-notice">编辑桌面共享媒体配置；Web 任务服务的运行地址由服务启动配置决定。</span>
    {draft && <>
      {fields.map(([field, label]) => <label key={field} className="df-field">{label}
        <input className="df-input" value={draft[field]} disabled={busy} maxLength={field.endsWith('Endpoint') || field === 'comfyUiBaseUrl' ? 2048 : 512}
          onChange={event => setDraft({ ...draft, [field]: event.target.value })} />
      </label>)}
      {(['image', 'video'] as const).map(kind => {
        const field = kind === 'image' ? 'imageApiKey' : 'videoApiKey'
        const clear = kind === 'image' ? 'clearImageKey' : 'clearVideoKey'
        return <div className="df-section" key={kind}>
          <label className="df-field">{kind === 'image' ? '图像' : '视频'}密钥（{draft[kind === 'image' ? 'imageHasKey' : 'videoHasKey'] ? '已配置' : '未配置'}；留空保留）
            <input className="df-input" type="password" autoComplete="new-password" value={keys[field]} disabled={busy || keys[clear]}
              onChange={event => setKeys({ ...keys, [field]: event.target.value })} />
          </label>
          <label><input type="checkbox" checked={keys[clear]} disabled={busy}
            onChange={event => setKeys({ ...keys, [clear]: event.target.checked, [field]: '' })} />清除此密钥</label>
        </div>
      })}
      <button className="df-mini-button" disabled={busy} onClick={() => void save()}>保存媒体设置</button>
    </>}
    <button className="df-mini-button" disabled={busy} onClick={() => void reload()}>重新加载媒体配置</button>
    {notice && <span className="df-notice" role="status">{notice}</span>}
  </div>
}

export function SettingsPanel({ canManage = false }: { canManage?: boolean }) {
  const [settings, setSettings] = useState<WebSettings | null>(null)
  const [error, setError] = useState('')
  const [probe, setProbe] = useState<ComfyUiProbe | null>(null)
  const [probeError, setProbeError] = useState('')
  const [probing, setProbing] = useState(false)

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

  if (error || !settings) return <div className="df-section">
    <ModelSettings canManage={canManage} />
    <div className={error ? 'df-notice is-error' : 'df-notice'}>{error ? `读不到设置：${error}` : '正在读取设置…'}</div>
  </div>

  return (
    <div className="df-section" style={{ gap: 14 }}>
      <ModelSettings canManage={canManage} />
      <MediaSettings canManage={canManage} />
      <SkillSettings canManage={canManage} />
      <div className="df-notice">下方为本次打开面板时读取的配置摘要。</div>
      {/* 探的是**这台服务端**配置里的 ComfyUI 地址，与上面表格里那台桌面机器配的地址可以不是同一台，
          所以标题里点明主语，免得用户拿桌面端的地址去对这里的结论。 */}
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
      {settingsGroups(settings).map((group) => (
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
