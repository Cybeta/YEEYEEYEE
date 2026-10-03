import { describe, expect, it } from 'vitest'
import { canvasChangeText, isOtherRevision, parseServerEvent, type CanvasChangedEvent } from '../src/shell/serverEvents'

/**
 * 服务端变更推送的读取口径（SSE 那条流）。
 *
 * 这些函数错了也不会抛错，只会**悄悄显示错**：把「别人改了」读成没有，界面就一直是「已同步」；
 * 反过来把「自己刚保存」读成别人改了，状态条会对着自己闪一下。两种都不报错，所以只能钉住。
 */

function canvasChanged(overrides: Record<string, unknown> = {}) {
  return JSON.stringify({
    type: 'canvas.changed',
    revision: 13,
    recordId: '11111111-1111-4111-8111-111111111111',
    actor: '陈默',
    scope: 'record',
    at: '2026-10-03T00:00:00.0000000+00:00',
    ...overrides
  })
}

/** 只收 canvas.changed；认不出来就是 null（与真实调用方同一种写法）。 */
function parseCanvas(data: string): CanvasChangedEvent | null {
  const event = parseServerEvent('canvas.changed', data)
  return event?.type === 'canvas.changed' ? event : null
}

describe('解析推来的事件', () => {
  it('正常的 canvas.changed 原样收下', () => {
    const event = parseCanvas(canvasChanged())
    expect(event).toMatchObject({ revision: 13, actor: '陈默', scope: 'record' })
  })

  it('修订不是数字就整条不认，不猜', () => {
    expect(parseCanvas(canvasChanged({ revision: '13' }))).toBeNull()
    expect(parseCanvas(canvasChanged({ revision: null }))).toBeNull()
    expect(parseCanvas(canvasChanged({ revision: undefined }))).toBeNull()
    // NaN / Infinity 也是 number，但它们排不出「谁更新」，同样不能收。
    expect(parseCanvas(canvasChanged({ revision: Number.NaN }))).toBeNull()
    expect(parseCanvas(canvasChanged({ revision: Number.POSITIVE_INFINITY }))).toBeNull()
  })

  it('没有记录号 = 整张画布都变了，收成 null 而不是空串', () => {
    expect(parseCanvas(canvasChanged({ recordId: '' }))?.recordId).toBeNull()
    expect(parseCanvas(canvasChanged({ recordId: undefined }))?.recordId).toBeNull()
    expect(parseCanvas(canvasChanged({ recordId: null }))?.recordId).toBeNull()
  })

  it('缺字段时补一个不撒谎的默认值', () => {
    const event = parseCanvas(canvasChanged({ actor: undefined, scope: undefined }))
    expect(event).toMatchObject({ actor: '有人', scope: 'record' })
  })

  it('edits.changed 也收，缺来源就说「有人」而不是空着', () => {
    const event = parseServerEvent('edits.changed', JSON.stringify({ type: 'edits.changed', reason: 'release' }))
    expect(event).toMatchObject({ type: 'edits.changed', reason: 'release', actor: '有人' })
  })

  it('data 里的 type 与事件名不一致就不认——两边总有一个错了', () => {
    expect(parseCanvas(canvasChanged({ type: 'edits.changed' }))).toBeNull()
    expect(parseServerEvent('edits.changed', JSON.stringify({ type: 'canvas.changed', revision: 3 }))).toBeNull()
  })

  it('以后服务端加新事件时，认不出的类型直接忽略而不是抛错', () => {
    expect(parseServerEvent('canvas.changed', JSON.stringify({ type: 'canvas.history', revision: 3 }))).toBeNull()
  })

  it('坏 JSON 与非对象 JSON 都安静地丢掉', () => {
    expect(parseCanvas('{ not json')).toBeNull()
    expect(parseCanvas('')).toBeNull()
    expect(parseCanvas('[1,2,3]')).toBeNull()
    expect(parseCanvas('"canvas.changed"')).toBeNull()
    expect(parseCanvas('null')).toBeNull()
  })
})

describe('「这条推送说的修订，和我手上的不是一个吗」', () => {
  const event: CanvasChangedEvent = { type: 'canvas.changed', revision: 13, recordId: null, actor: '陈默', scope: 'record', at: '' }

  it('不一样就说明我手上这份不是最新的', () => {
    expect(isOtherRevision(12, event)).toBe(true)
  })

  it('相等不算：自己保存成功后服务端也会广播一条，那条不该让状态条对着自己闪', () => {
    expect(isOtherRevision(13, event)).toBe(false)
  })

  /**
   * 这一条曾经写成「比手上的大才算」。项目模式的修订号是画布内容的哈希，
   * 新内容的哈希不保证比旧的大——用 `>` 判断时，真实的一次协作里就有推送被静默丢掉
   * （浏览器里亲眼看到的：服务端报 235064242561675，客户端手上是 280703274272840）。
   * 哈希排不出谁更新，所以判据只能是「不一样」。
   */
  it('比手上的小也要算——哈希排不出大小，拿它比大小会丢掉大约一半的推送', () => {
    expect(isOtherRevision(14, event)).toBe(true)
    expect(isOtherRevision(235_064_242_561_675, { ...event, revision: 280_703_274_272_840 })).toBe(true)
  })

  it('手上还没有修订号时不判断（画布都没加载，谈不上有没有变动）', () => {
    expect(isOtherRevision(undefined, event)).toBe(false)
  })
})

describe('把事件说成一句人话', () => {
  const base = { type: 'canvas.changed' as const, revision: 3, actor: '陈默', at: '' }

  it('整理布局就说整理布局', () => {
    expect(canvasChangeText({ ...base, recordId: null, scope: 'layout' })).toBe('陈默 重新整理了画布布局')
  })

  it('没有记录号就是整张画布', () => {
    expect(canvasChangeText({ ...base, recordId: null, scope: 'record' })).toBe('陈默 改动了画布')
  })

  it('知道是哪个节点就报出它的名字', () => {
    expect(canvasChangeText({ ...base, recordId: 'x', scope: 'record' }, '雾港来信')).toBe('陈默 改动了「雾港来信」')
  })

  it('不知道名字时也只说改了一个节点，不编名字', () => {
    expect(canvasChangeText({ ...base, recordId: 'x', scope: 'record' })).toBe('陈默 改动了 1 个节点')
  })
})
