import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { canCommitChatPreview, chatActionsKey, chatPreviewKey, readChatStream, type ChatMessage } from '../src/shell/chatStream'

describe('chat stream', () => {
  it('只允许预检成功的当前会话、消息和画布修订提交', () => {
    const message: ChatMessage = { id: 'm', role: 'assistant', content: '', thinking: '', state: 'completed', model: 'test', mode: 'approval', actionState: 'preview', baseRevision: 12 }
    const preview = { sessionId: 's', messageId: 'm', revision: 12, actions: chatActionsKey(message) }
    expect(canCommitChatPreview(message, preview, 's', 12, true)).toBe(true)
    expect(canCommitChatPreview(message, null, 's', 12, true)).toBe(false)
    expect(canCommitChatPreview(message, preview, 'other', 12, true)).toBe(false)
    expect(canCommitChatPreview({ ...message, id: 'other' }, preview, 's', 12, true)).toBe(false)
    expect(canCommitChatPreview(message, preview, 's', 13, true)).toBe(false)
    expect(canCommitChatPreview({ ...message, baseRevision: 11 }, preview, 's', 12, true)).toBe(false)
    expect(canCommitChatPreview({ ...message, actionErrors: ['预检失败'] }, preview, 's', 12, true)).toBe(false)
    expect(canCommitChatPreview({ ...message, actionState: 'discarded' }, preview, 's', 12, true)).toBe(false)
    expect(canCommitChatPreview({ ...message, mode: 'readonly' }, preview, 's', 12, true)).toBe(false)
    expect(canCommitChatPreview(message, preview, 's', 12, false)).toBe(false)
  })

  it('同一消息追加动作之后，按旧动作预检过的那份预览不能提交', () => {
    const base: ChatMessage = { id: 'm', role: 'assistant', content: '', thinking: '', state: 'completed', model: 'test', mode: 'approval', actionState: 'preview', baseRevision: 12,
      actions: [{ kind: 'update_node', target: 'n1', title: '改名', reason: 'Agent 提议' }] }
    // 预检那一刻只有普通修改动作。
    const preview = { sessionId: 's', messageId: 'm', revision: 12, actions: chatActionsKey(base) }
    expect(canCommitChatPreview(base, preview, 's', 12, true)).toBe(true)
    // 生成任务完成后服务端往同一条消息追加 attach_asset：消息 ID 与画布修订都没变。
    const appended: ChatMessage = { ...base, actions: [...base.actions!, { kind: 'attach_asset', target: 'n1', title: '生成媒体', reason: '生成产物' }] }
    expect(appended.id).toBe(base.id)
    expect(appended.baseRevision).toBe(base.baseRevision)
    expect(canCommitChatPreview(appended, preview, 's', 12, true)).toBe(false)
    // 重新预检（拿到新的动作身份）之后才允许提交。
    const repreviewed = { ...preview, actions: chatActionsKey(appended) }
    expect(canCommitChatPreview(appended, repreviewed, 's', 12, true)).toBe(true)
    // 动作被换掉（同样的条数、不同的动作）同样要重新预检。
    const swapped: ChatMessage = { ...base, actions: [{ kind: 'delete_node', target: 'n1', title: '删除', reason: 'Agent 提议' }] }
    expect(canCommitChatPreview(swapped, preview, 's', 12, true)).toBe(false)
  })

  it('只有内容变了（种类 / 目标 / 标题 / 原因都不变）也要重新预检', () => {
    // 服务端下发的动作对象比界面上明确用到的四个字段更多（content、entityTargets、aspectRatio…）。
    // attach_asset 正好是这一类：标题恒为「生成媒体」，变的只有它指向的那件产物。
    const withContent = (content: string) => ({ kind: 'attach_asset', target: 'n1', title: '生成媒体', reason: '生成产物', content }) as NonNullable<ChatMessage['actions']>[number]
    const first: ChatMessage = { id: 'm', role: 'assistant', content: '', thinking: '', state: 'completed', model: 'test', mode: 'approval', actionState: 'preview', baseRevision: 12,
      actions: [withContent('asset://first.png')] }
    const preview = { sessionId: 's', messageId: 'm', revision: 12, actions: chatActionsKey(first) }
    expect(canCommitChatPreview(first, preview, 's', 12, true)).toBe(true)
    const second: ChatMessage = { ...first, actions: [withContent('asset://second.png')] }
    expect(chatActionsKey(second)).not.toBe(chatActionsKey(first))
    expect(canCommitChatPreview(second, preview, 's', 12, true)).toBe(false)
    // 重新预检之后照旧可以提交。
    expect(canCommitChatPreview(second, { ...preview, actions: chatActionsKey(second) }, 's', 12, true)).toBe(true)
  })
  /**
   * 自动预检的身份 key。这里的每一条都对应一次真实故障：两处各写一份格式，格式还不一样，
   * 于是「预检过了」这件事永远记不住——预检一完成、busy 变回 false 让 effect 重跑，
   * 同一条审批又被预检一遍。
   */
  it('预检身份：同一份审批算出同一个值（否则会被反复预检）', () => {
    const message: ChatMessage = { id: 'm', role: 'assistant', content: '', thinking: '', state: 'completed', model: 'test', mode: 'approval', actionState: 'preview', baseRevision: 12,
      actions: [{ kind: 'update_node', target: 'n1', title: '改名', reason: 'Agent 提议' }] }
    // 两次调用（effect 里算一次、发请求时记一次）必须一模一样。
    expect(chatPreviewKey('s', message, 12)).toBe(chatPreviewKey('s', { ...message }, 12))
    // 四个维度都真的参与：会话、消息、修订、动作集合。
    expect(chatPreviewKey('s', message, 12)).not.toBe(chatPreviewKey('other', message, 12))
    expect(chatPreviewKey('s', message, 12)).not.toBe(chatPreviewKey('s', { ...message, id: 'other' }, 12))
    expect(chatPreviewKey('s', message, 12)).not.toBe(chatPreviewKey('s', message, 13))
    expect(chatPreviewKey('s', message, 12)).not.toBe(chatPreviewKey('s', { ...message, actions: [] }, 12))
    // 没有画布修订（新会话）也是稳定的一个值。
    expect(chatPreviewKey('s', message, null)).toBe(chatPreviewKey('s', message, undefined))
    expect(chatPreviewKey('s', message, null)).not.toBe(chatPreviewKey('s', message, 0))
  })

  it('动作集合变化后仍然允许重新预检（不是靠永久跳过换来的安静）', () => {
    const base: ChatMessage = { id: 'm', role: 'assistant', content: '', thinking: '', state: 'completed', model: 'test', mode: 'approval', actionState: 'preview', baseRevision: 12,
      actions: [{ kind: 'update_node', target: 'n1', title: '改名', reason: 'Agent 提议' }] }
    const appended: ChatMessage = { ...base, actions: [...base.actions!, { kind: 'attach_asset', target: 'n1', title: '生成媒体', reason: '生成产物' }] }
    // 同一条消息 + 同一个修订，只是动作集合变了 → 身份必须变，effect 因此会重新预检一次。
    expect(appended.id).toBe(base.id)
    expect(chatPreviewKey('s', appended, 12)).not.toBe(chatPreviewKey('s', base, 12))
  })

  it('ChatPanel 两处用的是同一个 key 生成入口，且判断没有被换成永久跳过', () => {
    const panel = readFileSync(new URL('../src/shell/ChatPanel.tsx', import.meta.url), 'utf8')
    // 算 key 与记 key 各自调用一次同一个函数（两处手写格式正是这次的故障）。
    expect(panel.match(/chatPreviewKey\(/g) ?? []).toHaveLength(2)
    expect(panel).toContain('attemptedPreview.current = chatPreviewKey(sessionId, message, baseRevision)')
    expect(panel).not.toContain('`${sessionId}:${messageId}:${baseRevision}`')
    // 判断仍是「这条审批预检过了就跳过，换一条/换了动作/换了修订就重来」。
    expect(panel).toContain('if (busy || streaming || attemptedPreview.current === approvalKey) return')
    expect(panel).toContain('attemptedPreview.current = approvalKey')
    // 只在这条审批**离开**（没有待审批对象）时忘掉它：撤销后恢复批准还要能重新预检一次。
    expect(panel).toContain("if (!approvalKey) { clearPreview(); attemptedPreview.current = ''; return }")
  })

  /**
   * 预检失败之后的三件事：清掉旧预览、把最新会话消息取回来（失败原因写在消息上）、
   * 迟到回包只动它自己的上下文。以及一件**不能**做的事：借着失败把「已经预检过谁」也抹掉——
   * 那会让自动预检在「失败 → 刷新 → 再触发」之间来回转。
   */
  it('预检失败后刷新会话并保留失败原因，且不会把自己变成自动重试的循环', () => {
    const panel = readFileSync(new URL('../src/shell/ChatPanel.tsx', import.meta.url), 'utf8')
    // 先清预览、再说原因，然后把最新会话消息取回来（失败结论与提示都挂在消息上）。
    expect(panel).toContain('clearPreview(); setNotice(errorMessage(e))')
    expect(panel).toMatch(/if \(operation === 'preview'\) \{\s*try \{ await refresh\(\) \}/)
    expect(panel).toContain('刷新会话失败')
    // 迟到的失败只认原会话与最新那一次预检：换了会话就什么都不做。
    expect(panel).toContain("currentContext.current.selected === sessionId && (operation !== 'preview' || run === previewRun.current)")
    // 「已经预检过谁」只在换会话、以及没有待审批对象这两处清（各一次）——失败路径不碰它，
    // 否则一次失败就会把同一个身份重新变成「没预检过」，自动预检立刻再来一轮。
    expect(panel.match(/attemptedPreview\.current = ''/g) ?? []).toHaveLength(2)
    const catchAt = panel.indexOf('} catch (e) {')
    const failureHandler = panel.slice(catchAt, panel.indexOf('} finally {', catchAt))
    expect(failureHandler).not.toContain('attemptedPreview')
  })

  it('节点执行固定来源并在 snapshot 消费，重试交给服务端恢复原上下文', () => {
    const panel = readFileSync(new URL('../src/shell/ChatPanel.tsx', import.meta.url), 'utf8')
    const app = readFileSync(new URL('../src/WebCanvasApp.tsx', import.meta.url), 'utf8')
    expect(app).toContain('prompt: suggestion.prompt, nodeId: target.recordId')
    expect(app).toContain('chapterId: chapterIdOf(target)')
    expect(app).toContain('baseRevision: scene.revision, title: recordTitle(target)')
    expect(panel).toContain("retry ? { retry: true }")
    expect(panel).toContain("let id = nodeRequest ? '' : selected")
    expect(panel).toContain('onNodeAgentConsumed?.(nodeRequest.id)')
    expect(panel).toContain('if (!ready || !nodeAgentRequest')
    expect(panel).toContain('[nodeAgentRequest, ready, busy, streaming, canChat]')
    expect(panel).toContain('run !== previewRun.current')
    expect(panel).toContain('if (inFlight.current || streaming || !message) return')
    // 参考图提示仍按「本条消息有没有来源节点」分岔（参数编辑器把它当 prop 传进去）。
    expect(panel).toContain('hasTargetNode={!!m.targetNodeId}')
  })
  it('preserves fragmented Chinese UTF-8, thinking, text, usage and terminal snapshot', async () => {
    const bytes = new TextEncoder().encode('event: thinking\ndata: {"text":"思考"}\n\nevent: text\ndata: {"text":"正文"}\n\nevent: usage\ndata: {"inputTokens":7}\n\nevent: done\ndata: {"id":"session"}\n\n')
    const stream = new ReadableStream<Uint8Array>({ start(controller) { for (let i = 0; i < bytes.length; i += 2) controller.enqueue(bytes.slice(i, i + 2)); controller.close() } })
    const events: unknown[] = []
    await readChatStream(new Response(stream), event => events.push(event))
    expect(events).toEqual([{ type: 'thinking', data: { text: '思考' } }, { type: 'text', data: { text: '正文' } }, { type: 'usage', data: { inputTokens: 7 } }, { type: 'done', data: { id: 'session' } }])
  })
  it('does not report a truncated stream as success', async () => {
    await expect(readChatStream(new Response('event: text\ndata: {"text":"partial"}\n\n'), () => {})).rejects.toThrow('连接中断')
  })
  it('统一生成与对话，只读无审批卡，生成结束仍刷新，提交刷新画布', () => {
    const panel = readFileSync(new URL('../src/shell/ChatPanel.tsx', import.meta.url), 'utf8')
    expect(panel).toContain("m.mode !== 'readonly' && !!m.actions?.length")
    expect(panel).toContain("m.skillState === 'submitted'")
    expect(panel).toContain('!streaming && !pendingGeneration')
    expect(panel).toContain('if (changed) await onChanged()')
    expect(panel).not.toMatch(/任务进度|重试任务|产物管理|skill-approval/)
  })
  it('reports backend failure before streaming', async () => {
    await expect(readChatStream(new Response('{"message":"模型不可用"}', { status: 503 }), () => {})).rejects.toThrow('模型不可用')
  })
})
