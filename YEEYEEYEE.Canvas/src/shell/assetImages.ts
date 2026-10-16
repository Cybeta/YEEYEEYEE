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

import { canvasUrl } from '../api'

/** `GET /api/web/entities/{entityId}/thumb`：可选带 variantId / versionId。 */
export function entityThumbUrl(entityId: string, variantId?: string | null, versionId?: string | null): string {
  const query = new URLSearchParams()
  // 空的参数**不要拼上去**：`?variantId=` 与「不带这个参数」在服务端是两种意思
  // （后者才是「默认变体 / 当前版本」）。
  if (variantId) query.set('variantId', variantId)
  if (versionId) query.set('versionId', versionId)
  const suffix = query.toString()
  return canvasUrl(`/api/web/entities/${encodeURIComponent(entityId)}/thumb${suffix ? `?${suffix}` : ''}`)
}

/** `GET /api/web/records/{recordId}/attachments/{attachmentId}/file`：节点某个产物的图片字节。 */
export function attachmentFileUrl(recordId: string, attachmentId: string): string {
  return canvasUrl(`/api/web/records/${encodeURIComponent(recordId)}/attachments/${encodeURIComponent(attachmentId)}/file`)
}

/**
 * `GET /api/web/jobs/{jobId}/outputs/{index}`：**任务自己**那件产物的字节。
 *
 * 与节点附件那条路的差别：候选图还没被采用，节点上没有它，也就没有附件 ID 可以拿来取图；
 * 而任务已经 Succeeded 的产物服务端允许按（任务 ID + 下标）读，归属与目录都在那一侧校验。
 */
export function jobOutputUrl(jobId: string, outputIndex: number): string {
  return canvasUrl(`/api/web/jobs/${encodeURIComponent(jobId)}/outputs/${outputIndex}`)
}

/**
 * `GET /api/web/entities/{entityId}/variants/{variantId}/media/{attachmentId}`：一条设定**某个版本**的媒体字节。
 *
 * 带 `versionId` 时服务端只在这个历史版本自己的附件里找（`EntityAttachmentFile`）——
 * 锁定版本的引用要读的就是那一份，所以这个参数不是可选的装饰：漏了它就会拿到当前变体的最新附件。
 */
export function entityMediaUrl(entityId: string, variantId: string, attachmentId: string, versionId?: string | null): string {
  const query = versionId ? `?versionId=${encodeURIComponent(versionId)}` : ''
  return canvasUrl(`/api/web/entities/${encodeURIComponent(entityId)}/variants/${encodeURIComponent(variantId)}/media/${encodeURIComponent(attachmentId)}${query}`)
}

/**
 * 重试时给地址加一个尝试序号。
 *
 * **不加就等于同一个 URL**，浏览器会直接把上一次的失败（负缓存）或旧结果拿出来，界面上按下「重试」
 * 什么都没发生。序号只影响浏览器这一侧的缓存键，服务端不认识也不看它（路由参数之外的多余查询参数一律忽略）。
 * 第 0 次（还没重试过）原样返回，免得正常加载的地址上多出一截没意义的参数。
 */
export function withRetry(url: string, attempt: number): string {
  return attempt > 0 ? `${url}${url.includes('?') ? '&' : '?'}retry=${attempt}` : url
}
