import { useEffect, useState } from 'react'
import { errorMessage, request } from '../api'
import { parseSettings, settingsGroups, type WebSettings } from './settingsView'

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

  if (error) return <div className="df-notice is-error">读不到设置：{error}</div>
  if (!settings) return <div className="df-notice">正在读取设置…</div>

  return (
    <div className="df-section" style={{ gap: 14 }}>
      <div className="df-notice">
        这一页只读：密钥在桌面端那台机器上（加密保存），这里只显示「配没配」。要改设置请到桌面端。
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
