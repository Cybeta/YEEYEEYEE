export type ChatUsage = { inputTokens: number; completionTokens: number; cacheHitTokens: number; cacheMissTokens: number }
export type ChatUsageReport = { chips: string[]; details: string[]; hasAnything: boolean; summary: string }
export type ChatMessage = { id: string; role: string; attachmentIds?: string[]; content: string; thinking: string; state: string; error?: string; usage?: ChatUsage; usageReport?: ChatUsageReport; contextWindow?: number; firstTokenSeconds?: number; decodeSeconds?: number; model: string; targetNodeId?: string | null; targetChapterId?: string | null; baseRevision?: number; source?: string; targetNodeTitle?: string; mode?: string; actionState?: string; committedRevision?: number; actions?: { kind: string; title: string; target: string; reason: string }[]; ask?: { question: string; options: string[] }; hints?: string[]; actionErrors?: string[]; skillActions?: { kind: string; target: string; content: string; reason: string; aspectRatio?: string; referenceAttachmentIds?: string[] }[]; skillState?: string; skillError?: string; skillJobIds?: string[] }
export type ChatSession = { id: string; title: string; updatedAt: string; messages: ChatMessage[]; jobIds?: string[] }
export type ChatEvent = { type: string; data: unknown }

/**
 * 这条消息**此刻**待审批的动作集合的身份。
 *
 * 为什么要有：同一条消息上的动作会在生成任务完成之后被追加（attach_asset），
 * 而消息 ID 与画布修订都可能不变。只看那两个判据，用户会在没看到新动作的情况下
 * 一并把它批了。权威判定在服务端（它按整批动作算指纹并在提交时核对），
 * 这里的这一份是**界面用的**：动作一变，虚影与批准按钮必须立刻失效，
 * 而不是等用户点下去才被服务端拒。
 *
 * 比的是**整个动作对象**，不是挑几个字段：字段清单是会走散的——原先只比 kind/target/title/reason，
 * 于是「只有 content 变了」的那一类（attach_asset 就是：标题恒为「生成媒体」，内容才是产物的引用）
 * 在界面上看不出变化，批准按钮照样能点，点下去才被服务端以 409 拒掉。
 * 整体串起来就与服务端的指纹同一个粒度：界面只会更早失效，绝不会比服务端更宽。
 */
export function chatActionsKey(message: ChatMessage): string {
  return JSON.stringify(message.actions ?? [])
}

/**
 * 一条待审批回复的**预检身份**：会话 + 审批消息 + 当前画布修订 + 这一刻的动作集合。
 *
 * 它只有一件事要做，但**两处必须算出同一个值**：自动预检的 effect 拿它判断
 * 「这条审批是不是已经预检过了」，发起预检时也用同一个值记下「我刚预检的是哪一份」。
 * 以前这两处各手写一份（effect 里四段、记录处三段、还漏了动作集合），格式本来就不一样——
 * 于是预检一结束、`busy` 变回 false 让 effect 重跑时，记下的值与算出来的值永远对不上，
 * 同一条审批就被反复预检。
 *
 * 所以这里只留**一个**生成入口：要让预检重新跑，就改这四个维度里的某一个
 * （换会话、换审批消息、画布修订变化、动作集合变化），不要在外面另拼一份格式。
 */
export function chatPreviewKey(sessionId: string, message: ChatMessage, revision: number | null | undefined): string {
  // 用 JSON 数组而不是拼冒号：动作集合本身就是一串 JSON，拼起来容易出现歧义（谁是谁的分界）。
  return JSON.stringify([sessionId, message.id, revision ?? null, chatActionsKey(message)])
}

export function canCommitChatPreview(message: ChatMessage, preview: { sessionId: string; messageId: string; revision: number; actions: string } | null, sessionId: string, revision: number | undefined, canCommit: boolean): boolean {
  return canCommit && message.mode !== 'readonly' && message.actionState === 'preview' && !message.actionErrors?.length
    && preview?.sessionId === sessionId && preview.messageId === message.id
    && preview.revision === revision && message.baseRevision === revision
    // 预览时的那批动作与此刻待执行的那批必须是同一批——追加之后就要求重新预检。
    && preview.actions === chatActionsKey(message)
}

// 保留跨网络块的UTF-8与SSE帧，不能把每次read当作一整条事件。
export async function readChatStream(response: Response, onEvent: (event: ChatEvent) => void): Promise<void> {
  if (!response.ok) {
    const failure = await response.json().catch(() => null) as { message?: string } | null
    throw new Error(failure?.message ?? `HTTP ${response.status}`)
  }
  if (!response.body) throw new Error('聊天流没有响应体')
  const reader = response.body.getReader(), decoder = new TextDecoder()
  let buffer = '', doneSeen = false
  try {
    for (;;) {
      const chunk = await reader.read()
      buffer += decoder.decode(chunk.value, { stream: !chunk.done })
      buffer = buffer.replace(/\r\n/g, '\n')
      let boundary: number
      while ((boundary = buffer.indexOf('\n\n')) >= 0) {
        const frame = buffer.slice(0, boundary); buffer = buffer.slice(boundary + 2)
        const lines = frame.split('\n')
        const type = lines.find(line => line.startsWith('event:'))?.slice(6).trim()
        const data = lines.filter(line => line.startsWith('data:')).map(line => line.slice(5).trimStart()).join('\n')
        if (type && data) { onEvent({ type, data: JSON.parse(data) }); if (type === 'done') doneSeen = true }
      }
      if (chunk.done) break
    }
    if (!doneSeen) throw new Error('连接中断，回复状态将从服务端恢复')
  } finally { await reader.cancel().catch(() => {}); reader.releaseLock() }
}
