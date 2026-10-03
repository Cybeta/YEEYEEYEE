import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { gestureHint, uiText, uiTextKeys } from '../src/shell/uiText'

/**
 * 两端共读的界面文案（画布手势提示这一片）。
 *
 * 重点不在「文案写得对不对」，而在**只有一份**：桌面端 csproj 里嵌的必须是同一个文件
 * （最后一条直接去读 csproj 对账）。两端的措辞与分隔符过去各写一份，已经走散过——
 * 一边说「点节点选中」，一边说「点击选中」；一边用「 · 」，一边用「 / 」。
 */
describe('共享界面文案', () => {
  it('措辞与分隔符一起共享，随手拼都不会拼出两种样子', () => {
    expect(uiText('separator')).toBe(' · ')
    expect(gestureHint('gesture.pan', 'gesture.select')).toBe('空白处拖拽平移 · 点节点或连线选中')
    expect(gestureHint('connect.mode', 'gesture.cancel')).toBe('连接模式：点另一个节点作为终点 · Esc 取消')
  })

  it('两端要用的那几句都在（少一句，总有一边会空一截）', () => {
    for (const key of [
      'gesture.pan', 'gesture.zoom', 'gesture.dragNode', 'gesture.connect',
      'gesture.select', 'gesture.delete', 'gesture.cancel', 'connect.mode'
    ]) {
      expect(uiText(key).length).toBeGreaterThan(0)
    }
  })

  it('键写错就当场炸，不静默少一截', () => {
    expect(() => uiText('gesture.nope')).toThrow(/没有这个键/)
    expect(uiTextKeys()).not.toContain('gesture.nope')
  })

  it('画布提示条真的走共享文案，不再在视图里写死一句', () => {
    const view = readFileSync(new URL('../src/shell/CanvasView.tsx', import.meta.url), 'utf8')
    expect(view).toContain("gestureHint('gesture.pan'")
    // 三个只会出现在那一行里的老措辞；「滚轮缩放」之类别的地方也在用，不作数。
    for (const literal of ['拖空白平移', '拖右缘圆点连线', '拖节点挪位置', '连接模式：再点一个节点作为终点'])
      expect(view).not.toContain(literal)
  })

  it('桌面端嵌的是同一个文件（C# 那边找不到第二份可抄）', () => {
    const csproj = readFileSync(
      new URL('../../YEEYEEYEE.Desktop.Avalonia/YEEYEEYEE.Desktop.Avalonia.csproj', import.meta.url), 'utf8')
    expect(csproj).toContain('..\\YEEYEEYEE.Canvas\\src\\shared\\uiText.json')
  })
})
