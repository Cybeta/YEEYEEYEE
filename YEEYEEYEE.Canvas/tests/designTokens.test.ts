import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import {
  BEGIN,
  END,
  check,
  cssValue,
  flatten,
  readTokens,
  renderCssPaletteBlock,
  renderXamlPaletteBlock,
  syncBlock,
  xamlKey,
  xamlValue
} from '../scripts/design-tokens.mjs'
import type { PaletteGroups } from '../scripts/design-tokens.mjs'

/**
 * 两端共读的设计令牌（工程债 #7）。
 *
 * 文案走散一眼能读出来，颜色差一个色阶、圆角差 2px，只有把两个产品并排看才发现——
 * 所以这一条的落点是「不可能悄悄走散」：
 *   ① 网页端那两个块（纯色、几何/圆角）与桌面端那个纯色块，都是 designTokens.json 的**产物**，
 *      `check()` 一比就知道谁手改过；
 *   ② 桌面端的几何与圆角散在 MainWindow.axaml 的元素属性与 App.axaml 的各个 Style 里，
 *      不是独立块、没法整块生成，所以那两项在这一侧是**对账**（下面那两条测试直接读文件对数）。
 */
const tokens = readTokens()
const flat = flatten(tokens)
const cssNames = flat.map((token) => token.css)

/** 取一个长度令牌的 px。 */
function px(name: string): number {
  const found = flat.find((token) => token.css === name)
  if (!found || found.kind !== 'length') throw new Error(`${name} 不是长度令牌`)
  return found.px
}

describe('共读设计令牌：designTokens.json 是唯一的一份', () => {
  it('名字规范：CSS 变量唯一；纯色令牌的画刷键能从变量名推出来', () => {
    // 令牌太少说明这份文件被掏空了，那这组测试就成了摆设。
    expect(flat.length).toBeGreaterThan(20)
    expect(new Set(cssNames).size).toBe(cssNames.length)
    for (const name of cssNames) expect(name).toMatch(/^--df-[a-z0-9-]+$/)

    const keys = flat.filter((token) => token.kind !== 'length').map((token) => xamlKey(token.css))
    expect(new Set(keys).size).toBe(keys.length)
    expect(xamlKey('--df-surface-1')).toBe('DfSurface1')
    expect(xamlKey('--df-glass-clear')).toBe('DfGlassClear')
  })

  it('值是规整的：颜色是 6 位十六进制 + 0-1 的 alpha；长度是整数 px', () => {
    for (const token of flat) {
      if (token.kind === 'length') {
        expect(Number.isInteger(token.px), token.css).toBe(true)
        expect(token.px, token.css).toBeGreaterThan(0)
        continue
      }
      expect(token.hex, token.css).toMatch(/^#[0-9a-f]{6}$/)
      expect(token.alpha, token.css).toBeGreaterThan(0)
      expect(token.alpha, token.css).toBeLessThanOrEqual(1)
    }
  })

  it('不透明写 6 位、带透明度换算成 rgba / #AARRGGBB——同一个 alpha，两种写法', () => {
    expect(cssValue({ hex: '#070a0f', alpha: 1 })).toBe('#070a0f')
    expect(xamlValue({ hex: '#070a0f', alpha: 1 })).toBe('#070A0F')
    // 0.65 * 255 = 165.75 → A6；这与改之前手写的 #A60D131B 逐位一致。
    expect(cssValue({ hex: '#0d131b', alpha: 0.65 })).toBe('rgba(13, 19, 27, 0.65)')
    expect(xamlValue({ hex: '#0d131b', alpha: 0.65 })).toBe('#A60D131B')
    expect(xamlValue({ hex: '#4d9bff', alpha: 0.055 })).toBe('#0E4D9BFF')
    // 长度：网页端写成 Npx。
    expect(cssValue({ kind: 'length', px: 244 })).toBe('244px')
  })

  it('三块产出都与来源同步（不一致就是有人手改过，跑 npm run tokens 重生成）', () => {
    expect(check()).toEqual([])
  })

  it('改一个颜色，两端的产物一起变——不需要记得去改第二处', () => {
    const tweaked: PaletteGroups = { groups: [{ name: 'Surfaces', kind: 'color', tokens: [{ css: '--df-bg', hex: '#123456', alpha: 1 }] }] }
    expect(renderCssPaletteBlock(tweaked)).toContain('--df-bg: #123456;')
    expect(renderXamlPaletteBlock(tweaked)).toContain('<SolidColorBrush x:Key="DfBg" Color="#123456" />')
  })

  it('桌面端那块真的在用它（不是留了个 marker 却没内容）', () => {
    const axaml = readFileSync(
      new URL('../../YEEYEEYEE.Desktop.Avalonia/App.axaml', import.meta.url), 'utf8')
    expect(axaml).toContain('<SolidColorBrush x:Key="DfGlass" Color="#A60D131B" />')
    expect(axaml).toContain('<SolidColorBrush x:Key="DfGridLine" Color="#0E4D9BFF" />')
  })

  it('生成器只碰 marker 之间：marker 少了就抛，不静默什么都不做', () => {
    expect(() => syncBlock('没有 marker 的文件', BEGIN, END, 'x')).toThrow(/marker/)
    const source = `a\n${BEGIN}\nold\n${END}\nb`
    expect(syncBlock(source, BEGIN, END, 'new')).toBe(`a\n${BEGIN}\nnew\n${END}\nb`)
  })

  it('桌面端外壳的几何与令牌对得上（MainWindow.axaml 那一个 Grid）', () => {
    const desktop = readFileSync(
      new URL('../../YEEYEEYEE.Desktop.Avalonia/MainWindow.axaml', import.meta.url), 'utf8')
    const root = /<Grid x:Name="WorkbenchRoot"[^>]*>/.exec(desktop)?.[0]
    if (!root) throw new Error('MainWindow.axaml 里找不到 WorkbenchRoot 那个 Grid')

    const margin = /Margin="(\d+)"/.exec(root)
    const columns = /ColumnDefinitions="(\d+),\*,(\d+)"/.exec(root)
    const rows = /RowDefinitions="(\d+),\*,(\d+)"/.exec(root)
    const minWidth = /MinWidth="(\d+)"/.exec(desktop)
    if (!margin || !columns || !rows || !minWidth) throw new Error('WorkbenchRoot 的几何属性没取全')

    const gap = px('--df-gap')
    expect(Number(margin[1])).toBe(gap)
    expect(Number(columns[1])).toBe(px('--df-rail-width'))
    expect(Number(columns[2])).toBe(px('--df-dock-width'))
    expect(Number(rows[1])).toBe(px('--df-titlebar-height'))
    expect(Number(rows[2])).toBe(px('--df-statusbar-height'))
    // 最小宽度不是随手取的数：两边边距 + 两条侧栏 + 中栏下限。
    expect(Number(minWidth[1])).toBe(
      gap * 2 + px('--df-rail-width') + px('--df-center-min') + px('--df-dock-width'))
  })

  it('桌面端常用的圆角与令牌对得上（散在 App.axaml 的 Style 里，只能对数）', () => {
    const axaml = readFileSync(
      new URL('../../YEEYEEYEE.Desktop.Avalonia/App.axaml', import.meta.url), 'utf8')

    const cornerRadiusOf = (selector: string): number => {
      const start = axaml.indexOf(`<Style Selector="${selector}">`)
      if (start < 0) throw new Error(`App.axaml 里找不到 ${selector}`)
      const block = axaml.slice(start, axaml.indexOf('</Style>', start))
      const match = /Property="CornerRadius" Value="(\d+)"/.exec(block)
      if (!match) throw new Error(`${selector} 里找不到 CornerRadius`)
      return Number(match[1])
    }

    const map: Record<string, string> = {
      'Border.glass': '--df-radius-glass',
      'Border.node': '--df-radius-panel',
      'Button.navButton': '--df-radius-box',
      'Button.toolButton': '--df-radius-control',
      'Button.miniButton': '--df-radius-small',
      'ComboBox.panelCombo': '--df-radius-small',
      'Border.chip': '--df-radius-pill',
      'Button.chip': '--df-radius-pill'
    }
    // 映射表太短说明这条测试在空转。
    expect(Object.keys(map).length).toBeGreaterThan(5)
    for (const [selector, token] of Object.entries(map))
      expect(cornerRadiusOf(selector), `${selector} ↔ ${token}`).toBe(px(token))
  })

  it('只收纯色：渐变与投影仍在各自那一侧手写，没被混进这份令牌表', () => {
    for (const cssOnly of ['--df-aurora', '--df-scan', '--df-glow-line', '--df-glow-shadow'])
      expect(cssNames, cssOnly).not.toContain(cssOnly)
    for (const name of ['--df-radius-glass', '--df-gap'])
      expect(cssNames, name).toContain(name)
  })
})
