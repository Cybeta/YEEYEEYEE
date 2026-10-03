/**
 * 引用徽标上那张小图从哪来。
 *
 * 服务端只投影**名称**（`thumbnailRef` 那个字符串也一并给了，但它是画布文件里的写法：
 * 可能是 `asset://x.png`，也可能是绝对路径，还可能是一段 data URL）。**这些东西不能直接塞进 `<img src>`**：
 * 浏览器读不到服务端的文件系统，而且把画布里的路径直接当 URL 用等于让数据决定去读哪个文件。
 *
 * 所以缩略图走一条按 **ID** 取图的接口：客户端只说「哪条设定的哪个变体哪一版」，
 * 由服务端解析并检查目录。这个模块就负责**把这三个 ID 拼成那个 URL**——
 * 拼错了不会报错，只会静默显示不出图，所以它单独成文件、有测试。
 */

/** `GET /api/web/entities/{entityId}/thumb`：可选带 variantId / versionId。 */
export function entityThumbUrl(entityId: string, variantId?: string | null, versionId?: string | null): string {
  const query = new URLSearchParams()
  // 空的参数**不要拼上去**：`?variantId=` 与「不带这个参数」在服务端是两种意思
  // （后者才是「默认变体 / 当前版本」）。
  if (variantId) query.set('variantId', variantId)
  if (versionId) query.set('versionId', versionId)
  const suffix = query.toString()
  return `/api/web/entities/${encodeURIComponent(entityId)}/thumb${suffix ? `?${suffix}` : ''}`
}
