import { describe, expect, it } from 'vitest'
import { followAction, hasUnsavedDraft } from '../src/shell/serverEvents'

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
