// 给 scripts/design-tokens.mjs 的类型声明：脚本本身是 .mjs（要能直接 `node` 跑，
// 不引入 ts 运行时），但测试与 `tsc --noEmit` 需要知道它导出了什么。

export type Token = { css: string; hex: string; alpha: number }
export type TokenGroup = { name: string; tokens: Token[] }
export type TokenTable = { note: string[]; groups: TokenGroup[] }
/** 只关心颜色本身时用的最小形状（cssValue / xamlValue 只需要它）。 */
export type Palette = { hex: string; alpha: number }
/** 只要分组结构即可渲染（渲染器不读 note）。 */
export type PaletteGroups = { groups: TokenGroup[] }

/** 生成块边界的关键字（在 tokens.css / App.axaml 里各有一对注释）。 */
export const BEGIN: string
export const END: string

/** 读那份唯一的调色板（src/shared/designTokens.json）。 */
export function readTokens(): TokenTable

/** `--df-surface-1` → `DfSurface1`。 */
export function xamlKey(cssVar: string): string

/** CSS 那一侧的值：不透明 `#rrggbb`，带透明度 `rgba(r, g, b, a)`。 */
export function cssValue(token: Palette): string

/** XAML 那一侧的值：不透明 `#RRGGBB`，带透明度 `#AARRGGBB`。 */
export function xamlValue(token: Palette): string

/** tokens.css 里那一块（不含缩进）。 */
export function renderCssBlock(tokens: PaletteGroups): string

/** App.axaml 里那一块（不含缩进）。 */
export function renderXamlBlock(tokens: PaletteGroups): string

/** 只替换 begin / end 之间的内容，缩进沿用 marker 那一行；marker 不全就抛。 */
export function syncBlock(source: string, begin: string, end: string, block: string): string

/** 对账两个产物文件，返回不同步的文件名（空数组 = 一致）。 */
export function check(): string[]
