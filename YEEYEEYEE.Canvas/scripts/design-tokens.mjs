// 设计令牌的生成器：把 src/shared/designTokens.json 那一份，摊成两端的块。
//
//   node scripts/design-tokens.mjs --write     重生成（改完 designTokens.json 就跑它）
//   node scripts/design-tokens.mjs --check     只对账，不一致就非零退出（CI / 测试里用）
//
// 为什么是「生成」而不是「两端各读一份」：网页端是一段 CSS、桌面端是一组 XAML 画刷，
// **格式不同，没法共用同一个物理文件**。所以共用的是那份 JSON，两端各自的块都是它的产物——
// 改一处忘了另一处这件事，从「靠人记得」变成「测试会红」。
//
// 产出三块：
//   tokens.css  palette:begin/end  —— 纯色（CSS 变量）
//   tokens.css  metrics:begin/end  —— 几何与圆角（CSS 变量）
//   App.axaml   palette:begin/end  —— 纯色（SolidColorBrush）
// 生成器只重写 marker 之间的内容，marker 以外的部分（渐变、字体、Fluent 覆盖）一概不碰。
//
// 桌面端的几何与圆角**不在**这里生成：它们散在 MainWindow.axaml 的元素属性与 App.axaml 的
// 各个 Style 里，不是一个独立块。那两项在那一侧靠 tests/designTokens.test.ts 对数。

import { readFileSync, writeFileSync } from 'node:fs'

const ROOT = new URL('../', import.meta.url)
const TOKENS = new URL('src/shared/designTokens.json', ROOT)
const CSS = new URL('src/shell/tokens.css', ROOT)
const XAML = new URL('../YEEYEEYEE.Desktop.Avalonia/App.axaml', ROOT)

/** 纯色块的边界（tokens.css 与 App.axaml 各一对）。 */
export const BEGIN = 'palette:begin'
export const END = 'palette:end'

/** 几何与圆角块的边界（只有 tokens.css 有）。 */
export const METRICS_BEGIN = 'metrics:begin'
export const METRICS_END = 'metrics:end'

/** 读那份唯一的令牌表。 */
export function readTokens() {
  return JSON.parse(readFileSync(TOKENS, 'utf8'))
}

/** 平坦化：group 只用来分组注释，产出时按顺序摊开。 */
export function flatten(tokens) {
  return withKind(tokens.groups).flatMap((group) =>
    group.tokens.map((token) => ({ ...token, group: group.name })))
}

/** 纯色组（桌面端只认纯色：几何/圆角在那一侧不是独立块）。 */
const colorGroups = (tokens) => tokens.groups.filter((group) => group.kind === 'color')

/** `--df-surface-1` → `DfSurface1`（桌面端画刷的键名就是从 CSS 变量名推出来的）。 */
export function xamlKey(cssVar) {
  const parts = cssVar.replace(/^--df-/, '').split('-')
  return 'Df' + parts.map((part) => part.charAt(0).toUpperCase() + part.slice(1)).join('')
}

/** 一位小数的 alpha → 两位十六进制（0.65 → 165.75 → A6）。 */
function alphaHex(alpha) {
  return Math.round(alpha * 255).toString(16).padStart(2, '0').toUpperCase()
}

function cssRgb(token) {
  const hex = token.hex.replace('#', '')
  return [0, 2, 4].map((i) => parseInt(hex.slice(i, i + 2), 16)).join(', ')
}

/** CSS 那一侧的值：长度写 `Npx`；不透明写 6 位；带透明度写 rgba()（alpha 按 JSON 里的小数原样打印）。 */
export function cssValue(token) {
  if (token.kind === 'length') return `${token.px}px`
  return token.alpha === 1 ? token.hex : `rgba(${cssRgb(token)}, ${token.alpha})`
}

/** XAML 那一侧的值：不透明写 #RRGGBB，带透明度写 #AARRGGBB。 */
export function xamlValue(token) {
  const hex = token.hex.replace('#', '').toUpperCase()
  return token.alpha === 1 ? `#${hex}` : `#${alphaHex(token.alpha)}${hex}`
}

/** 把组上的 kind 落到每个令牌上（令牌本身可以不重复写 kind）。 */
const withKind = (groups) =>
  groups.map((group) => ({
    ...group,
    tokens: group.tokens.map((token) => ({ ...token, kind: token.kind ?? group.kind }))
  }))

/** 按分组摊开，组与组之间空一行；令牌上的 note 变成它前面的一行注释。 */
function renderGroups(groups, renderLine, comment) {
  return withKind(groups)
    .map((group) => [
      comment(group.name),
      ...group.tokens.flatMap((token) => [
        ...(token.note ? [comment(token.note)] : []),
        renderLine(token)
      ])
    ].join('\n'))
    .join('\n\n')
}

/** tokens.css 里的纯色块（不含缩进，缩进由 syncBlock 补）。 */
export function renderCssPaletteBlock(tokens) {
  return renderGroups(colorGroups(tokens), (token) => `${token.css}: ${cssValue(token)};`, (text) => `/* ${text} */`)
}

/** tokens.css 里的几何与圆角块（不含缩进）。 */
export function renderCssMetricsBlock(tokens) {
  const metrics = tokens.groups.filter((group) => group.kind !== 'color')
  return renderGroups(metrics, (token) => `${token.css}: ${cssValue(token)};`, (text) => `/* ${text} */`)
}

/** App.axaml 里的纯色块（不含缩进）。 */
export function renderXamlPaletteBlock(tokens) {
  return renderGroups(
    colorGroups(tokens),
    (token) => `<SolidColorBrush x:Key="${xamlKey(token.css)}" Color="${xamlValue(token)}" />`,
    (text) => `<!-- ${text} -->`)
}

/**
 * 把块写回文件：只替换 begin / end 两行之间的内容，缩进沿用 marker 那一行。
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

/** 三块产出：文件、块名、渲染出来的内容。 */
function outputs(tokens) {
  return [
    ['src/shell/tokens.css（纯色）', CSS, renderCssPaletteBlock(tokens), BEGIN, END],
    ['src/shell/tokens.css（几何/圆角）', CSS, renderCssMetricsBlock(tokens), METRICS_BEGIN, METRICS_END],
    ['YEEYEEYEE.Desktop.Avalonia/App.axaml（纯色）', XAML, renderXamlPaletteBlock(tokens), BEGIN, END]
  ]
}

/** 对账：返回还在不同步的块名（空 = 全都在同步）。 */
export function check() {
  const tokens = readTokens()
  return outputs(tokens)
    .filter(([, url, block, begin, end]) => syncBlock(readFileSync(url, 'utf8'), begin, end, block) !== readFileSync(url, 'utf8'))
    .map(([name]) => name)
}

/** 重生成：按 marker 逐块写回（同一文件的两块各自替换）。 */
export function write() {
  const tokens = readTokens()
  let css = readFileSync(CSS, 'utf8')
  css = syncBlock(css, BEGIN, END, renderCssPaletteBlock(tokens))
  css = syncBlock(css, METRICS_BEGIN, METRICS_END, renderCssMetricsBlock(tokens))
  writeFileSync(CSS, css)
  writeFileSync(XAML, syncBlock(readFileSync(XAML, 'utf8'), BEGIN, END, renderXamlPaletteBlock(tokens)))
}

const invokedDirectly = process.argv[1] && process.argv[1].endsWith('design-tokens.mjs')
if (invokedDirectly) {
  const tokens = readTokens()
  if (process.argv.includes('--write')) {
    write()
    const colors = flatten(tokens).filter((token) => token.kind === 'color').length
    console.log(`已重生成：${colors} 个颜色令牌 + ${flatten(tokens).length - colors} 个尺寸/圆角令牌`)
  } else {
    const stale = check()
    if (stale.length) {
      console.error(`与 designTokens.json 不一致：${stale.join('、')}（跑 npm run tokens 重生成）`)
      process.exit(1)
    }
    console.log('令牌一致')
  }
}
