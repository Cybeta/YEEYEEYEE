import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import {
  BEGIN,
  END,
  check,
  cssValue,
  readTokens,
  renderCssBlock,
  renderXamlBlock,
  syncBlock,
  xamlKey,
  xamlValue
} from '../scripts/design-tokens.mjs'

/**
 * 配色那条债（工程债 #7）：两端各写一份色板，改一边忘一边，而且**看不出来**——
 * 文案走散一眼能读出来，颜色差一个色阶只有把两个产品并排看才发现。
 *
 * 现在唯一的一份是 `src/shared/designTokens.json`，两端各自的色块都是它的**产物**
 * （`scripts/design-tokens.mjs`）。这一组测试盯的就是「产物跟来源一致」——
 * 谁手改了 tokens.css 或 App.axaml 里那两个块，这里当场红。
 */
const tokens = readTokens()

/** 平坦化，方便逐个查。 */
const flat = tokens.groups.flatMap((group) => group.tokens.map((token) => ({ ...token, group: group.name })))

describe('共读色板：designTokens.json 是唯一的一份', () => {
  it('名字规范：CSS 变量唯一、画刷键能从变量名推出来', () => {
    // 扫到的令牌太少说明这份文件被掏空了，那这组测试就成了摆设。
    expect(flat.length).toBeGreaterThan(10)

    const css = flat.map((token) => token.css)
    expect(new Set(css).size).toBe(css.length)
    for (const token of flat) expect(token.css).toMatch(/^--df-[a-z0-9-]+$/)

    const keys = flat.map((token) => xamlKey(token.css))
    expect(new Set(keys).size).toBe(keys.length)
    expect(xamlKey('--df-surface-1')).toBe('DfSurface1')
    expect(xamlKey('--df-glass-clear')).toBe('DfGlassClear')
  })

  it('值是规整的：6 位十六进制 + 0-1 的 alpha', () => {
    for (const token of flat) {
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
  })

  it('两端那两个色块与来源同步（不一致就是有人手改过，跑 npm run tokens 重生成）', () => {
    expect(check()).toEqual([])
  })

  it('改一个颜色，两端的产物一起变——不需要记得去改第二处', () => {
    const tweaked = { groups: [{ name: 'Surfaces', tokens: [{ css: '--df-bg', hex: '#123456', alpha: 1 }] }] }
    expect(renderCssBlock(tweaked)).toContain('--df-bg: #123456;')
    expect(renderXamlBlock(tweaked)).toContain('<SolidColorBrush x:Key="DfBg" Color="#123456" />')
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
})
