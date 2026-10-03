import shared from '../shared/uiText.json'

/**
 * 两端共读的界面文案。
 *
 * **唯一的一份**在 `src/shared/uiText.json`：网页端直接 import 它，桌面端用 EmbeddedResource
 * 把同一个文件嵌进去（见 `YEEYEEYEE.Desktop.Avalonia.csproj` 里那一条 Include）。
 *
 * 为什么文件住在网页端项目里：TS 只能 import 项目内的文件（项目外的路径在 dev server 上会被拦），
 * 而 MSBuild 嵌哪个路径的文件都行——所以让文件迁就 TS，C# 侧多写一行。反过来放，
 * 网页端要么得配 dev server 的白名单，要么得抄一份，两条路都比这一行差。
 *
 * 这里**不做兜底**：读不到某个键就直接报错。界面文案缺一句会静默地少一截，
 * 而少掉的那截没人会发现——共享文件里的键写错是开发期错误，该当场炸。
 */
const table = shared as Record<string, string>

export function uiText(key: string): string {
  const value = table[key]
  if (typeof value !== 'string' || value.length === 0) {
    throw new Error(`共享文案里没有这个键：${key}（YEEYEEYEE.Canvas/src/shared/uiText.json）`)
  }
  return value
}

/**
 * 把若干个手势拼成一行提示。
 *
 * 分隔符也在共享文件里：两端各写一个「 · 」看着一样，但改起来就不一样了——
 * 这一条与每一句措辞一样，都是「口径」。
 */
export function gestureHint(...keys: string[]): string {
  return keys.map(uiText).join(uiText('separator'))
}

/** 共享文件里的所有键，供测试与排查使用。 */
export function uiTextKeys(): string[] {
  return Object.keys(table)
}
