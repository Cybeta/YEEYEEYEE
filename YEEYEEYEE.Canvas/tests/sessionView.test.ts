import { describe, expect, it } from 'vitest'
import {
  canEdit, canManageUsers, describeUser, gateOf, parseAuthState, parseUser, roleLabel,
  sessionNotice, validateLogin, validatePassword, validateSetup, validateUsername,
  type AuthState
} from '../src/SessionView'

const empty: AuthState = { initialized: false, setupTokenRequired: false, passwordMinLength: 8, user: null }
const initialised: AuthState = { ...empty, initialized: true }
const signedIn = (role: 'Admin' | 'Editor' | 'Viewer'): AuthState => ({
  ...initialised,
  user: { id: 'u1', username: 'lin', displayName: '林晚', role }
})

describe('Web session state', () => {
  it('reads the three states apart, and never treats a missing user as signed in', () => {
    expect(gateOf(empty)).toBe('setup')
    expect(gateOf(initialised)).toBe('login')
    expect(gateOf(signedIn('Admin'))).toBe('ready')
    // 没有 initialized 字段的响应不能猜成「已初始化」——猜错的后果是把人挡在登录页且没法建号。
    expect(() => parseAuthState({ user: null })).toThrow('响应格式不正确')
    expect(() => parseAuthState(null)).toThrow('响应格式不正确')
  })

  it('parses the user and falls back to the lowest role for an unknown one', () => {
    expect(parseUser({ id: 'u', username: 'lin', role: 'Admin', displayName: '林晚' })).toMatchObject({ role: 'Admin', displayName: '林晚' })
    // 认不出的角色一律按只读：服务端也这么判，两边不能一个松一个紧。
    expect(parseUser({ id: 'u', username: 'lin', role: 'Root' }).role).toBe('Viewer')
    expect(parseUser({ id: 'u', username: 'lin' }).role).toBe('Viewer')
    // 没有 id 的响应不算一个用户。注意要包成函数再断言，直接调用会把异常抛在断言之外。
    expect(() => parseUser({ username: 'lin' })).toThrow()
  })

  it('gives edit rights to Admin and Editor only, user management to Admin only', () => {
    expect([canEdit('Admin'), canEdit('Editor'), canEdit('Viewer')]).toEqual([true, true, false])
    expect([canManageUsers('Admin'), canManageUsers('Editor'), canManageUsers('Viewer')]).toEqual([true, false, false])
    expect([roleLabel('Admin'), roleLabel('Editor'), roleLabel('Viewer')]).toEqual(['管理员', '编辑', '只读'])
    expect(describeUser({ id: 'u', username: 'lin', displayName: '林晚', role: 'Editor' })).toBe('林晚（编辑）')
    // 没有显示名时退回用户名，不能显示空白。
    expect(describeUser({ id: 'u', username: 'lin', displayName: '', role: 'Viewer' })).toBe('lin（只读）')
  })

  it('validates the setup form with the same rules as the server', () => {
    expect(validateSetup({ username: 'ab', password: 'longenough', confirm: 'longenough', setupToken: '', displayName: '' }, empty)).toBe('用户名至少 3 个字符')
    // 三个汉字长度够了，但字符集不允许——长度与字符集是两条独立的规则，都要各自命中。
    expect(validateSetup({ username: '林晚晚', password: 'longenough', confirm: 'longenough', setupToken: '', displayName: '' }, empty)).toBe('用户名只能用字母、数字与 . _ -')
    expect(validateSetup({ username: 'lin', password: 'short', confirm: 'short', setupToken: '', displayName: '' }, empty)).toBe('口令至少 8 位')
    expect(validateSetup({ username: 'lin', password: 'longenough', confirm: 'longenough2', setupToken: '', displayName: '' }, empty)).toBe('两次输入的口令不一致')
    expect(validateSetup({ username: 'lin', password: 'longenough', confirm: 'longenough', setupToken: '', displayName: '' }, empty)).toBeNull()
    // 部署要求初始化令牌时，空着就不放行。
    expect(validateSetup({ username: 'lin', password: 'longenough', confirm: 'longenough', setupToken: '', displayName: '' }, { ...empty, setupTokenRequired: true }))
      .toBe('这次部署要求填初始化令牌')
    // 已经建过号就不该再走这条表单，如实说出来而不是提交后拿 409。
    expect(validateSetup({ username: 'lin', password: 'longenough', confirm: 'longenough', setupToken: '', displayName: '' }, initialised))
      .toBe('管理员已经建过了，请直接登录')
    expect(validateUsername('a'.repeat(33))).toBe('用户名最多 32 个字符')
    expect(validatePassword('', 8)).toBe('口令不能为空')
  })

  it('validates the login form and writes a notice that names the signed-in user', () => {
    expect(validateLogin({ username: '', password: 'x' })).toBe('请填用户名')
    expect(validateLogin({ username: 'lin', password: '' })).toBe('请填口令')
    expect(validateLogin({ username: 'lin', password: 'x' })).toBeNull()
    expect(sessionNotice(empty)).toContain('第一个账号就是管理员')
    expect(sessionNotice(initialised)).toContain('登录后')
    expect(sessionNotice(signedIn('Editor'))).toContain('林晚（编辑）')
  })
})
