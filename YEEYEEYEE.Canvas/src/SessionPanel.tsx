import { useState } from 'react'
import {
  canManageUsers, describeUser, gateOf, roleLabel, sessionNotice, validateLogin, validateSetup,
  type AuthState, type SessionUser, type UserRole
} from './SessionView'
import './session.css'

/**
 * 登录 / 首次建号 / 账号管理。**全部走会话 cookie**：
 * 服务端把登录态放在 HttpOnly cookie 里，前端不保存任何令牌，
 * 所以刷新页面仍然登录着，而脚本也读不到凭据（这正是不用 localStorage 存令牌的原因）。
 */

type Notice = { kind: 'info' | 'error' | 'success'; message: string }

async function send<T>(path: string, method: 'GET' | 'POST' | 'PATCH' | 'DELETE', body?: object): Promise<T> {
  const response = await fetch(path, {
    method,
    credentials: 'same-origin',
    ...(body ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {}),
    cache: 'no-store'
  })
  const data: unknown = await response.json().catch(() => null)
  if (!response.ok) {
    const failure = data && typeof data === 'object' ? data as { code?: unknown; message?: unknown } : {}
    throw new Error(`${typeof failure.code === 'string' ? `[${failure.code}] ` : ''}${typeof failure.message === 'string' ? failure.message : `HTTP ${response.status}`}`)
  }
  return data as T
}

export function SessionPanel({ state, onChanged }: { state: AuthState; onChanged: () => void }) {
  const gate = gateOf(state)
  const [username, setUsername] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [setupToken, setSetupToken] = useState('')
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<Notice>({ kind: 'info', message: sessionNotice(state) })

  async function run(action: () => Promise<Notice>) {
    if (busy) return
    setBusy(true)
    try { setNotice(await action()) }
    catch (error) { setNotice({ kind: 'error', message: error instanceof Error ? error.message : '未知错误' }) }
    finally { setBusy(false) }
  }

  function clearSecrets() {
    setPassword('')
    setConfirm('')
    setSetupToken('')
  }

  if (gate === 'setup') return <div className="session-panel">
    <div className="session-card">
      <h2>建立管理员账号</h2>
      <p className="session-hint">这个服务还没有账号。<strong>你建的第一个账号就是管理员</strong>，之后由它来建别人的账号。</p>
      <form onSubmit={(event) => {
        event.preventDefault()
        const input = { username, password, confirm, setupToken, displayName }
        const invalid = validateSetup(input, state)
        if (invalid) { setNotice({ kind: 'error', message: invalid }); return }
        void run(async () => {
          await send('/api/auth/setup', 'POST', { username: username.trim(), password, displayName: displayName.trim() })
          clearSecrets()
          onChanged()
          return { kind: 'success', message: '管理员已建立，正在进入画布…' }
        })
      }}>
        <label>用户名<input value={username} autoComplete="username" onChange={(event) => setUsername(event.target.value)} placeholder="字母、数字与 . _ -" /></label>
        <label>显示名（可留空）<input value={displayName} autoComplete="off" onChange={(event) => setDisplayName(event.target.value)} placeholder="留空就用用户名" /></label>
        <label>口令<input type="password" value={password} autoComplete="new-password" onChange={(event) => setPassword(event.target.value)} placeholder={`至少 ${state.passwordMinLength} 位`} /></label>
        <label>再输一次<input type="password" value={confirm} autoComplete="new-password" onChange={(event) => setConfirm(event.target.value)} /></label>
        {state.setupTokenRequired && <label>初始化令牌<input value={setupToken} autoComplete="off" onChange={(event) => setSetupToken(event.target.value)} placeholder="部署时配置的 X-Setup-Token" /></label>}
        <button type="submit" disabled={busy}>{busy ? '正在建立…' : '建号并登录'}</button>
      </form>
      <p className={`web-notice ${notice.kind}`} role="status">{notice.message}</p>
    </div>
  </div>

  if (gate === 'login') return <div className="session-panel">
    <div className="session-card">
      <h2>登录</h2>
      <p className="session-hint">登录后才能查看与编辑这个画布。</p>
      <form onSubmit={(event) => {
        event.preventDefault()
        const invalid = validateLogin({ username, password })
        if (invalid) { setNotice({ kind: 'error', message: invalid }); return }
        void run(async () => {
          await send('/api/auth/login', 'POST', { username: username.trim(), password })
          clearSecrets()
          onChanged()
          return { kind: 'success', message: '登录成功' }
        })
      }}>
        <label>用户名<input value={username} autoComplete="username" onChange={(event) => setUsername(event.target.value)} /></label>
        <label>口令<input type="password" value={password} autoComplete="current-password" onChange={(event) => setPassword(event.target.value)} /></label>
        <button type="submit" disabled={busy}>{busy ? '正在登录…' : '登录'}</button>
      </form>
      <p className={`web-notice ${notice.kind}`} role="status">{notice.message}</p>
    </div>
  </div>

  const user = state.user!
  return <div className="session-bar">
    <span className="session-who">{describeUser(user)}</span>
    <button type="button" disabled={busy} onClick={() => void run(async () => {
      await send('/api/auth/logout', 'POST')
      onChanged()
      return { kind: 'info', message: '已退出登录' }
    })}>退出登录</button>
    <ChangePassword busy={busy} run={run} />
    {canManageUsers(user.role) && <UserAdmin busy={busy} run={run} />}
    <p className={`web-notice ${notice.kind}`} role="status">{notice.message}</p>
  </div>
}

function ChangePassword({ busy, run }: { busy: boolean; run: (action: () => Promise<Notice>) => Promise<void> }) {
  const [open, setOpen] = useState(false)
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  if (!open) return <button type="button" onClick={() => setOpen(true)}>改口令</button>
  return <span className="session-inline">
    <input type="password" value={current} placeholder="当前口令" autoComplete="current-password" onChange={(event) => setCurrent(event.target.value)} />
    <input type="password" value={next} placeholder="新口令" autoComplete="new-password" onChange={(event) => setNext(event.target.value)} />
    <button type="button" disabled={busy || !current || !next} onClick={() => void run(async () => {
      await send('/api/auth/password', 'POST', { currentPassword: current, newPassword: next })
      setCurrent(''); setNext(''); setOpen(false)
      // 服务端改完口令会把这个人的会话全部作废，所以这里必须回到登录页。
      location.reload()
      return { kind: 'success', message: '口令已修改' }
    })}>保存</button>
    <button type="button" onClick={() => { setOpen(false); setCurrent(''); setNext('') }}>取消</button>
  </span>
}

function UserAdmin({ busy, run }: { busy: boolean; run: (action: () => Promise<Notice>) => Promise<void> }) {
  const [open, setOpen] = useState(false)
  const [users, setUsers] = useState<SessionUser[]>([])
  const [newName, setNewName] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [newRole, setNewRole] = useState<UserRole>('Editor')

  async function reload(): Promise<Notice> {
    const result = await send<{ users: SessionUser[] }>('/api/auth/users', 'GET')
    setUsers(result.users)
    return { kind: 'success', message: `共 ${result.users.length} 个账号` }
  }

  if (!open) return <button type="button" onClick={() => { setOpen(true); void run(reload) }}>账号管理</button>
  return <div className="session-users">
    <strong>账号管理</strong>
    <ul>
      {users.map((item) => <li key={item.id}>
        <code>{item.username}</code><span>{roleLabel(item.role)}</span>
        {item.disabled && <b role="alert">已停用</b>}
        <select value={item.role} disabled={busy} onChange={(event) => void run(async () => {
          await send(`/api/auth/users/${item.id}`, 'PATCH', { role: event.target.value })
          return await reload()
        })}>
          <option value="Admin">管理员</option><option value="Editor">编辑</option><option value="Viewer">只读</option>
        </select>
        <button type="button" disabled={busy} onClick={() => void run(async () => {
          await send(`/api/auth/users/${item.id}`, 'PATCH', { disabled: !item.disabled })
          return await reload()
        })}>{item.disabled ? '启用' : '停用'}</button>
        <button type="button" disabled={busy} onClick={() => {
          const password = window.prompt(`给 ${item.username} 设一个新口令（至少 8 位）`)
          if (!password) return
          void run(async () => {
            await send(`/api/auth/users/${item.id}/password`, 'POST', { newPassword: password })
            return { kind: 'success', message: `${item.username} 的口令已重置，它已登录的会话同时失效` }
          })
        }}>重置口令</button>
        <button type="button" disabled={busy} onClick={() => {
          if (!window.confirm(`删除账号 ${item.username}？它的登录态会立刻失效。`)) return
          void run(async () => { await send(`/api/auth/users/${item.id}`, 'DELETE'); return await reload() })
        }}>删除</button>
      </li>)}
    </ul>
    <form onSubmit={(event) => {
      event.preventDefault()
      void run(async () => {
        await send('/api/auth/users', 'POST', { username: newName.trim(), password: newPassword, role: newRole, displayName: newName.trim() })
        setNewName(''); setNewPassword('')
        return await reload()
      })
    }}>
      <input value={newName} placeholder="新用户名" onChange={(event) => setNewName(event.target.value)} />
      <input type="password" value={newPassword} placeholder="初始口令（至少 8 位）" onChange={(event) => setNewPassword(event.target.value)} />
      <select value={newRole} onChange={(event) => setNewRole(event.target.value as UserRole)}>
        <option value="Editor">编辑</option><option value="Viewer">只读</option><option value="Admin">管理员</option>
      </select>
      <button type="submit" disabled={busy || !newName.trim() || newPassword.length < 8}>建号</button>
    </form>
    <p className="session-hint">最后一个管理员不能降级、停用或删除——否则就没人能管账号了。</p>
    <button type="button" onClick={() => setOpen(false)}>收起</button>
  </div>
}
