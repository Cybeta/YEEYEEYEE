import { useEffect, useState } from 'react'
import { errorMessage, request } from '../api'
import { parseSettings, settingsGroups, parseComfyUiProbe, describeComfyUiProbe, type ComfyUiProbe, type WebSettings } from './settingsView'

/**
 * 设置（网页端）：**只读**。
 *
 * 为什么只有读：密钥在盘上是 DPAPI 密文，而这个服务端跑在 Linux 上（目标框架 net10.0），根本解不开；
 * 就算解得开也不该解——那属于桌面端那台机器。所以这一页回答的是「现在配了什么、密钥配没配」，
 * 要改请到桌面端。这不是「这一版没做完」，是这条链的边界。
 *
 * 它**自己取数据**（不像画布那样由 WebCanvasApp 统一持有）：一条只读接口、不参与任何写路径、
 * 也不进画布状态——挂进那边只会让那个已经很长的组件再长一截。
 */
export function SettingsPanel() {
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

  if (error) return <div className="df-notice is-error">读不到设置：{error}</div>
  if (!settings) return <div className="df-notice">正在读取设置…</div>

  return (
    <div className="df-section" style={{ gap: 14 }}>
      <div className="df-notice">
        这一页只读：密钥在桌面端那台机器上（加密保存），这里只显示「配没配」。要改设置请到桌面端。
      </div>
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
