import { describe, expect, it } from 'vitest'
import { canApplyScene, followAction, hasUnsavedDraft } from '../src/shell/serverEvents'

/**
 * 收到「画布有变动」之后该做什么。
 *
 * 与桌面端 `RemoteCanvasChange.Decide` 是同一条闸门，理由也一样：**自动跟上会替换手上这份**。
 * 判错了不报错，只会静静地把用户刚敲进去的几行丢掉——所以这里逐条钉住。
 */

describe('别人改了画布之后', () => {
  it('本地干净：自动跟上', () => {
    expect(followAction(true, false)).toBe('reload')
  })

  it('手上有草稿：只说，不动', () => {
    expect(followAction(true, true)).toBe('notify')
  })

  it('不是别人改的（自己那次保存的回声）：什么都不做', () => {
    expect(followAction(false, false)).toBe('ignore')
    expect(followAction(false, true)).toBe('ignore')
  })
})

describe('「手上有草稿」的判据', () => {
  const record = { title: '分镜一', content: '内容' }

  it('编辑框聚焦中就算（点进去看了一眼也是）', () => {
    expect(hasUnsavedDraft(true, { title: '分镜一', content: '内容' }, record)).toBe(true)
  })

  it('标题或内容与记录不一样就算（改完点了别处也算）', () => {
    expect(hasUnsavedDraft(false, { title: '分镜一改了', content: '内容' }, record)).toBe(true)
    expect(hasUnsavedDraft(false, { title: '分镜一', content: '内容改了' }, record)).toBe(true)
  })

  it('没选中任何节点、又没聚焦：不算有草稿', () => {
    expect(hasUnsavedDraft(false, { title: '随便', content: '随便' }, null)).toBe(false)
  })

  it('聚焦中但没改：仍然算有草稿（他正要改）', () => {
    expect(hasUnsavedDraft(true, { title: '分镜一', content: '内容' }, record)).toBe(true)
  })
})

/**
 * 产物回挂之后的那次后台刷新（followScene）取回来的场景还能不能落到界面上。
 *
 * 这条路是**自动**的：用户没按任何按钮，界面就换了一份数据。所以两道闸门都得钉住——
 * 落错了不报错，只是悄悄把他正看着的东西换掉、或者把他刚敲的几行顶没。
 */
describe('后台刷新回来的场景能不能落到界面上', () => {
  it('还是最新那次、手上也没在编辑：可以落', () => {
    expect(canApplyScene({ stale: false, editing: false })).toBe(true)
  })

  it('期间整份重载过 / 又跟了别的版本（这次已经过期）：不落——落上去就是回退', () => {
    expect(canApplyScene({ stale: true, editing: false })).toBe(false)
    expect(canApplyScene({ stale: true, editing: true })).toBe(false)
  })

  it('请求往返期间用户已经打开编辑框：不落——那会把他刚敲的东西顶掉', () => {
    expect(canApplyScene({ stale: false, editing: true })).toBe(false)
  })
})
