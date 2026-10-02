/**
 * Web 端登录态的判断逻辑。**这里全是纯函数**，不碰 DOM、不发请求，所以能直接测——
 * 界面上「该显示登录还是显示画布」这种判断一旦写错，表现是「打不开」或者「没登录也能改」，
 * 这两种都不该靠手点来发现。
 *
 * 注意：下面这些校验规则在服务端也有一份，**服务端那份才是权威**，
 * 这里的副本只为了在用户敲键盘时就给出反馈，不做任何安全判断。
 */

export type UserRole = 'Admin' | 'Editor' | 'Viewer'

export type SessionUser = {
  id: string
  username: string
  displayName: string
  role: UserRole
  roleLabel?: string
  disabled?: boolean
  createdAt?: string
  lastLoginAt?: string | null
}

export type AuthState = {
  initialized: boolean
  setupTokenRequired: boolean
  passwordMinLength: number
  user: SessionUser | null
}

function objectOf(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
}

function asRole(value: unknown): UserRole {
  return value === 'Admin' || value === 'Editor' ? value : 'Viewer'
}

/** 解析 `/api/auth/state` 的响应。形状不对就抛，让界面显示「响应格式不正确」而不是猜一个默认值。 */
export function parseAuthState(value: unknown): AuthState {
  const raw = objectOf(value)
  if (typeof raw.initialized !== 'boolean') throw new Error('响应格式不正确')
  return {
    initialized: raw.initialized,
    setupTokenRequired: raw.setupTokenRequired === true,
    passwordMinLength: typeof raw.passwordMinLength === 'number' && raw.passwordMinLength > 0 ? raw.passwordMinLength : 8,
    user: raw.user ? parseUser(raw.user) : null
  }
}

export function parseUser(value: unknown): SessionUser {
  const raw = objectOf(value)
  if (typeof raw.id !== 'string' || typeof raw.username !== 'string') throw new Error('响应格式不正确')
  return {
    id: raw.id,
    username: raw.username,
    displayName: typeof raw.displayName === 'string' && raw.displayName ? raw.displayName : raw.username,
    role: asRole(raw.role),
    roleLabel: typeof raw.roleLabel === 'string' ? raw.roleLabel : undefined,
    disabled: raw.disabled === true,
    createdAt: typeof raw.createdAt === 'string' ? raw.createdAt : undefined,
    lastLoginAt: typeof raw.lastLoginAt === 'string' ? raw.lastLoginAt : null
  }
}

/** 能改画布的角色。与 `UserRoles.ClaimsFor` 的判定必须一致：只读角色在服务端也拿不到 canvas.edit。 */
export function canEdit(role: UserRole): boolean {
  return role === 'Admin' || role === 'Editor'
}

export function canManageUsers(role: UserRole): boolean {
  return role === 'Admin'
}

export function roleLabel(role: UserRole): string {
  return role === 'Admin' ? '管理员' : role === 'Editor' ? '编辑' : '只读'
}

export function describeUser(user: SessionUser): string {
  return `${user.displayName || user.username}（${roleLabel(user.role)}）`
}

/** 用户名规则与服务端 `UserStore.CheckUsername` 对齐：3–32 位，只允许字母数字与 . _ -。 */
export function validateUsername(value: string): string | null {
  const trimmed = value.trim()
  if (trimmed.length < 3) return '用户名至少 3 个字符'
  if (trimmed.length > 32) return '用户名最多 32 个字符'
  if (!/^[A-Za-z0-9._-]+$/.test(trimmed)) return '用户名只能用字母、数字与 . _ -'
  return null
}

export function validatePassword(value: string, minLength: number): string | null {
  if (!value) return '口令不能为空'
  if (value.length < minLength) return `口令至少 ${minLength} 位`
  return null
}

export type SetupInput = { username: string; password: string; confirm: string; setupToken: string; displayName: string }

/** 首次建号的表单校验。返回第一条不通过的原因，全通过返回 null。 */
export function validateSetup(input: SetupInput, state: AuthState): string | null {
  if (!state.initialized) {
    const usernameError = validateUsername(input.username)
    if (usernameError) return usernameError
    const passwordError = validatePassword(input.password, state.passwordMinLength)
    if (passwordError) return passwordError
    if (input.password !== input.confirm) return '两次输入的口令不一致'
    if (state.setupTokenRequired && !input.setupToken.trim()) return '这次部署要求填初始化令牌'
    return null
  }
  return '管理员已经建过了，请直接登录'
}

export type LoginInput = { username: string; password: string }

export function validateLogin(input: LoginInput): string | null {
  if (!input.username.trim()) return '请填用户名'
  if (!input.password) return '请填口令'
  return null
}

/** 首屏该让用户做什么。三种状态互斥，界面按它分叉，不要在组件里再判一遍。 */
export type Gate = 'setup' | 'login' | 'ready'

export function gateOf(state: AuthState): Gate {
  if (state.user) return 'ready'
  return state.initialized ? 'login' : 'setup'
}

export function sessionNotice(state: AuthState): string {
  const gate = gateOf(state)
  if (gate === 'setup') return '这个服务还没有账号。你建的第一个账号就是管理员。'
  if (gate === 'login') return '登录后才能查看与编辑这个画布。'
  return `已登录：${describeUser(state.user!)}`
}
