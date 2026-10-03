import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

/**
 * 工作台的窄窗口处理（原工程债 #9，第 164 轮已修）。
 *
 * 这一条是**纯样式**：渲染出来长什么样只能靠眼睛看。但有两件事是死的、可以钉住——
 * ① 中栏得有一个下限（退回 `minmax(0, 1fr)` 就等于把这一条改回了坏的那版）；
 * ② 那个下限与桌面端窗口的最小宽度必须是**同一个数**推出来的。
 * 两端各定一个数，迟早会对不上——配色两份就是这么来的，这里不重复那个错误。
 */
const tokens = readFileSync(new URL('../src/shell/tokens.css', import.meta.url), 'utf8')
const desktopWindow = readFileSync(new URL('../../YEEYEEYEE.Desktop.Avalonia/MainWindow.axaml', import.meta.url), 'utf8')

/** 取一条 CSS 变量的像素值。 */
function cssPx(name: string): number {
  const match = new RegExp(`${name}:\\s*(\\d+)px`).exec(tokens)
  if (!match) throw new Error(`tokens.css 里找不到 ${name}`)
  return Number(match[1])
}

/** `.df-workbench { ... }` 那一条声明块（不含 .is-dock-closed 那条）。 */
function workbenchBlock(): string {
  const start = tokens.indexOf('.df-workbench {')
  if (start < 0) throw new Error('tokens.css 里找不到 .df-workbench 的声明块')
  return tokens.slice(start, tokens.indexOf('}', start))
}

describe('窄窗口处理：中栏的最小宽度', () => {
  it('中栏有下限，不是 minmax(0, 1fr)——那会把它压到 min-content，画布被挤成一条', () => {
    expect(workbenchBlock()).toContain('minmax(var(--df-center-min), 1fr)')
    expect(tokens).not.toContain('minmax(0, 1fr) var(--df-dock-width)')
  })

  it('收起右侧栏时同样保留下限（那一版也曾经是 minmax(0, 1fr)）', () => {
    const closed = tokens.slice(tokens.indexOf('.df-workbench.is-dock-closed'))
    expect(closed.slice(0, closed.indexOf('}', closed.indexOf('{')))).toContain('minmax(var(--df-center-min), 1fr)')
  })

  it('装不下时改成横向滚动，而不是裁掉右边栏', () => {
    const block = workbenchBlock()
    expect(block).toContain('overflow-x: auto')
    expect(block).not.toContain('overflow: hidden')
  })

  it('两端同一个数：桌面端窗口最小宽度 − 两条边距 = 侧栏 + 中栏下限 + 侧栏', () => {
    const windowMatch = /MinWidth="(\d+)"/.exec(desktopWindow)
    if (!windowMatch) throw new Error('MainWindow.axaml 里找不到 Window 的 MinWidth')
    const marginMatch = /x:Name="WorkbenchRoot"\s+Margin="(\d+)"/.exec(desktopWindow)
    if (!marginMatch) throw new Error('MainWindow.axaml 里找不到 WorkbenchRoot 的 Margin')

    // 网页端那边 .df-workbench 的 padding 也是同一个 --df-gap，所以两边的账能对上。
    expect(Number(marginMatch[1])).toBe(cssPx('--df-gap'))
    expect(Number(windowMatch[1]) - cssPx('--df-gap') * 2).toBe(
      cssPx('--df-rail-width') + cssPx('--df-center-min') + cssPx('--df-dock-width'))
  })
})
