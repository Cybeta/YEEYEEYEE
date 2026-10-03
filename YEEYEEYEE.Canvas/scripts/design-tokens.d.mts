// 给 scripts/design-tokens.mjs 的类型声明：脚本本身是 .mjs（要能直接 `node` 跑，
// 不引入 ts 运行时），但测试与 `tsc --noEmit` 需要知道它导出了什么。

/** 纯色令牌。 */
export type ColorToken = { css: string; hex: string; alpha: number; kind?: 'color'; note?: string }
/** 尺寸令牌（px）。kind 必须写出来，测试靠它做窄化。 */
export type LengthToken = { css: string; px: number; kind: 'length'; note?: string }

/** 读 JSON 时用的宽松形状（组上的 kind 不必落到每个令牌上）。 */
export type RawToken = { css: string; hex?: string; alpha?: number; px?: number; kind?: 'color' | 'length'; note?: string }
export type TokenGroup = { name: string; kind?: 'color' | 'length'; tokens: RawToken[] }
export type TokenTable = { note?: string[]; groups: TokenGroup[] }

/** 摊平后的令牌（带组名与 kind）。 */
export type FlatToken =
  | (ColorToken & { group: string })
  | (LengthToken & { group: string })

/** 只关心颜色本身时用的最小形状。 */
export type Palette = { hex: string; alpha: number }
/** 只关心分组结构时用的最小形状（渲染器不读 note）。 */
export type PaletteGroups = { groups: TokenGroup[] }

/** 纯色块的边界（tokens.css 与 App.axaml 各一对）。 */
export const BEGIN: string
export const END: string

/** 几何与圆角块的边界（只有 tokens.css 有）。 */
export const METRICS_BEGIN: string
export const METRICS_END: string

/** 读那份唯一的令牌表。 */
export function readTokens(): TokenTable

/** 摊平全部令牌（组上的 kind 会落到每个令牌上）。 */
export function flatten(tokens: TokenTable): FlatToken[]

/** `--df-surface-1` → `DfSurface1`。 */
export function xamlKey(cssVar: string): string

/** CSS 那一侧的值：长度写 `Npx`；不透明写 `#rrggbb`；带透明度写 `rgba(r, g, b, a)`。 */
export function cssValue(token: { kind?: string; hex?: string; alpha?: number; px?: number }): string

/** XAML 那一侧的值：不透明 `#RRGGBB`，带透明度 `#AARRGGBB`。 */
export function xamlValue(token: Palette): string

/** tokens.css 里的纯色块（不含缩进）。 */
export function renderCssPaletteBlock(tokens: PaletteGroups): string

/** tokens.css 里的几何与圆角块（不含缩进）。 */
export function renderCssMetricsBlock(tokens: PaletteGroups): string

/** App.axaml 里的纯色块（不含缩进）。 */
export function renderXamlPaletteBlock(tokens: PaletteGroups): string

/** 只替换 begin / end 之间的内容，缩进沿用 marker 那一行；marker 不全就抛。 */
export function syncBlock(source: string, begin: string, end: string, block: string): string

/** 对账三块产出，返回还没同步的块名（空数组 = 一致）。 */
export function check(): string[]

/** 按 marker 把三块产出写回文件。 */
export function write(): void
