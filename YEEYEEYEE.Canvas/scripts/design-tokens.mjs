// 调色板的生成器：把 src/shared/designTokens.json 那一份，摊成两端的两个色块。
//
//   node scripts/design-tokens.mjs --write     重生成（改完 designTokens.json 就跑它）
//   node scripts/design-tokens.mjs --check     只对账，不一致就非零退出（CI / 测试里用）
//
// 为什么是「生成」而不是「两端各读一份」：网页端是一段 CSS、桌面端是一组 XAML 画刷，
// **格式不同，没法共用同一个物理文件**。所以共用的是那份 JSON，两端各自的色块都是它的产物——
// 改一处忘了另一处这件事，从「靠人记得」变成「测试会红」。
//
// 那几个「块」的边界在文件里各有一对 marker（palette:begin / palette:end），
// 生成器只重写 marker 之间的内容，marker 以外的部分（几何、渐变、Fluent 覆盖）一概不碰。

import { readFileSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const ROOT = new URL('../', import.meta.url)
const TOKENS = new URL('src/shared/designTokens.json', ROOT)
const CSS = new URL('src/shell/tokens.css', ROOT)
const XAML = new URL('../YEEYEEYEE.Desktop.Avalonia/App.axaml', ROOT)

export const BEGIN = 'palette:begin'
export const END = 'palette:end'

/** 读那份唯一的调色板。 */
export function readTokens() {
  return JSON.parse(readFileSync(TOKENS, 'utf8'))
}

/** 平坦化：group 只用来分组注释，产出时按顺序摊开。 */
function flatten(tokens) {
  return tokens.groups.flatMap((group) => group.tokens.map((token) => ({ ...token, group: group.name })))
}

/** `--df-surface-1` → `DfSurface1`（桌面端画刷的键名就是从 CSS 变量名推出来的）。 */
export function xamlKey(cssVar) {
  const parts = cssVar.replace(/^--df-/, '').split('-')
  return 'Df' + parts.map((part) => part.charAt(0).toUpperCase() + part.slice(1)).join('')
}

/** 一位小数的 alpha → 两位十六进制（0.65 → 165.75 → A6）。 */
function alphaHex(alpha) {
  return Math.round(alpha * 255).toString(16).padStart(2, '0').toUpperCase()
}

/** CSS 那一侧的值：不透明写 6 位，带透明度写 rgba()——alpha 按 JSON 里写的小数原样打印。 */
export function cssValue(token) {
  return token.alpha === 1 ? token.hex : `rgba(${cssRgb(token)}, ${token.alpha})`
}

function cssRgb(token) {
  const hex = token.hex.replace('#', '')
  return [0, 2, 4].map((i) => parseInt(hex.slice(i, i + 2), 16)).join(', ')
}

/** XAML 那一侧的值：不透明写 #RRGGBB，带透明度写 #AARRGGBB。 */
export function xamlValue(token) {
  const hex = token.hex.replace('#', '').toUpperCase()
  return token.alpha === 1 ? `#${hex}` : `#${alphaHex(token.alpha)}${hex}`
}

/** 按分组摊开，组与组之间空一行；传入的 renderer 决定每一行长什么样。 */
function renderGroups(tokens, renderLine, comment) {
  return tokens.groups
    .map((group) => [comment(group.name), ...group.tokens.map(renderLine)].join('\n'))
    .join('\n\n')
}

/** tokens.css 里那一块（不含缩进，缩进由 syncBlock 补）。 */
export function renderCssBlock(tokens) {
  return renderGroups(tokens, (token) => `${token.css}: ${cssValue(token)};`, (name) => `/* ${name} */`)
}

/** App.axaml 里那一块（不含缩进）。 */
export function renderXamlBlock(tokens) {
  return renderGroups(
    tokens,
    (token) => `<SolidColorBrush x:Key="${xamlKey(token.css)}" Color="${xamlValue(token)}" />`,
    (name) => `<!-- ${name} -->`)
}

/**
 * 把块写回文件：只替换 begin / end 两行之间的内容，两端的缩进各自沿用原来那一行。
 * marker 少了一个就直接抛——静默不替换正是「改了一边没生效」那种最难发现的情况。
 */
export function syncBlock(source, begin, end, block) {
  const lines = source.split('\n')
  const b = lines.findIndex((line) => line.includes(begin))
  const e = lines.findIndex((line) => line.includes(end))
  if (b < 0 || e <= b) throw new Error(`找不到 ${begin} / ${end} 这一对 marker`)
  const indent = lines[b].match(/^\s*/)[0]
  const body = block.split('\n').map((line) => (line ? indent + line : line))
  return [...lines.slice(0, b + 1), ...body, ...lines.slice(e)].join('\n')
}

/** 对账：返回不一致的文件名列表（空 = 都在同步）。 */
export function check() {
  const tokens = readTokens()
  return [
    ['src/shell/tokens.css', CSS, renderCssBlock(tokens)],
    ['YEEYEEYEE.Desktop.Avalonia/App.axaml', XAML, renderXamlBlock(tokens)]
  ]
    .map(([name, url, block]) => {
      const source = readFileSync(url, 'utf8')
      return syncBlock(source, BEGIN, END, block) === source ? null : name
    })
    .filter(Boolean)
}

const invokedDirectly = process.argv[1] && process.argv[1].endsWith('design-tokens.mjs')
if (invokedDirectly) {
  const tokens = readTokens()
  if (process.argv.includes('--write')) {
    writeFileSync(CSS, syncBlock(readFileSync(CSS, 'utf8'), BEGIN, END, renderCssBlock(tokens)))
    writeFileSync(XAML, syncBlock(readFileSync(XAML, 'utf8'), BEGIN, END, renderXamlBlock(tokens)))
    console.log(`已重生成 ${flatten(tokens).length} 个颜色令牌（tokens.css / App.axaml）`)
  } else {
    const stale = check()
    if (stale.length) {
      console.error(`色板与 designTokens.json 不一致：${stale.join('、')}（跑 npm run tokens 重生成）`)
      process.exit(1)
    }
    console.log('色板一致')
  }
}
