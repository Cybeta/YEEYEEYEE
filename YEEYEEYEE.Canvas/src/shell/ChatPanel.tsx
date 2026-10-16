import { useEffect, useMemo, useRef, useState } from 'react'
import { canvasUrl, errorMessage, request } from '../api'
import type { AttachmentSummary } from '../assets'
import { canCommitChatPreview, chatActionsKey, chatPreviewKey, readChatStream, type ChatMessage, type ChatSession } from './chatStream'
import { ChatUsageRow } from './ChatUsageRow'
import './agentPreview.css'
import { attachmentFileUrl, entityThumbUrl } from './assetImages'

import type { AgentPanelProps, NodeAgentRequest } from './AgentPanel'
import {
  agentPreviewAssetDiffs, changeLabel, parseAgentPreview, previewAttachmentFileUrl,
  referenceLabel, referenceModeText, type AgentCanvasPreview, type AttachmentDelta, type ReferenceDelta
} from './agentPreview'
import { attachmentKindLabel } from './nodeMedia'
import type { ViewRecord } from './records'

type GeneratedJob = { state: string; outputs: { role: string; ref: string }[] }

function GeneratedOutputs({ jobId }: { jobId: string }) {
  const [job, setJob] = useState<GeneratedJob | null>(null)
  const [error, setError] = useState('')
  useEffect(() => {
    const controller = new AbortController()
    void request<GeneratedJob>(`/api/web/jobs/${jobId}`, { signal: controller.signal })
      .then(result => { if (!controller.signal.aborted) setJob(result) })
      .catch(e => { if (!controller.signal.aborted) setError(errorMessage(e)) })
    return () => controller.abort()
  }, [jobId])
  return <div className="df-section">
    {error && <span role="alert" className="df-notice is-error">生成产物加载失败：{error}</span>}
    {job?.state === 'Succeeded' && job.outputs.map((output, index) => {
      const url = canvasUrl(`/api/web/jobs/${jobId}/outputs/${index}`)
      const image = output.role === 'image' || /\.(png|jpe?g|webp)$/i.test(output.ref)
      const video = output.role === 'video' || /\.(mp4|webm|mov)$/i.test(output.ref)
      return <div key={index} className="df-section">
        {image && <a href={url} target="_blank" rel="noreferrer"><img src={url} alt={`生成图片 ${index + 1}`} style={{ display: 'block', maxWidth: '100%', maxHeight: 360, objectFit: 'contain' }} onError={() => setError('图片无法读取，请检查产物文件')} /></a>}
        {video && <video src={url} controls preload="metadata" style={{ maxWidth: '100%', maxHeight: 360 }} onError={() => setError('视频无法读取，请检查产物文件')} />}
        <a href={url} target="_blank" rel="noreferrer">查看生成{image ? '图片' : video ? '视频' : '产物'} {index + 1}</a>
      </div>
    })}
  </div>
}

export function ChatPanel({ userId, canChat, chapterId, nodeId, baseRevision, canCommit, records, nodeAgentRequest, onNodeAgentConsumed, onPreviewChange, onChanged }: AgentPanelProps) {
  const [mode, setMode] = useState('approval')
  const [imageRatios, setImageRatios] = useState<Record<string, string>>({})
  async function generateImage(messageId: string) {
    if (inFlight.current || streaming || !canChat) return
    const sessionId = selected
    inFlight.current = true
    setBusy(true); setNotice('')
    try {
      await request(`/api/web/chat/sessions/${sessionId}/skills/${messageId}/invoke`, { method: 'POST', body: JSON.stringify({ aspectRatio: imageRatios[messageId] }) })
      await refresh()
    } catch (e) { clearPreview(); setNotice(errorMessage(e)); await refresh().catch(() => {}) } finally { inFlight.current = false; setBusy(false) }
  }
  /**
   * 手上这一份**已通过预检**的预览。`actions` 是预检那一刻待执行动作的身份：
   * 生成任务完成后服务端会往同一条消息追加 attach_asset，消息与修订都不变——
   * 靠它才能认出「批准资格已经过期」（见 chatStream.chatActionsKey）。
   */
  const [preview, setPreview] = useState<(AgentCanvasPreview & { actions: string }) | null>(null)
  const [approvalId, setApprovalId] = useState('')
  const previewRun = useRef(0)
  const previewController = useRef<AbortController | null>(null)
  const attemptedPreview = useRef('')
  const consumedRequests = useRef(new Set<string>())
  const inFlight = useRef(false)
  const executionSession = useRef('')
  const currentContext = useRef({ selected: '', baseRevision, canCommit })
  const previewCallback = useRef(onPreviewChange)
  previewCallback.current = onPreviewChange
  function clearPreview() {
    previewRun.current++
    previewController.current?.abort()
    setPreview(null)
    previewCallback.current?.(null)
  }
  const [sessions, setSessions] = useState<ChatSession[]>([])
  const [ready, setReady] = useState(false)
  const [selected, setSelected] = useState('')
  const [text, setText] = useState('')
  const [attachments, setAttachments] = useState<{ id: string; name: string; type: string }[]>([])
  async function upload(file: File) {
    setBusy(true); setNotice('')
    try {
      if (attachments.length >= 8) throw new Error('每条消息最多8个附件')
      if (file.size > (file.type.startsWith('image/') ? 5242880 : 65536)) throw new Error('图片最多5MiB，文本最多64KiB')
      let id = selected
      if (!id) { const next = await request<ChatSession>('/api/web/chat/sessions', { method: 'POST' }); id = next.id; update(next); setSelected(id) }
      const response = await fetch(canvasUrl(`/api/web/chat/sessions/${id}/attachments?name=${encodeURIComponent(file.name)}`), { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': file.type || 'text/plain' }, body: file })
      const result = await response.json()
      if (!response.ok) throw new Error(result.message)
      setAttachments(current => [...current, result])
    } catch (e) { setNotice(errorMessage(e)) } finally { setBusy(false) }
  }
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const active = useRef<AbortController | null>(null)
  const mounted = useRef(true)
  const knownCommits = useRef(new Map<string, number>())
  const history = useRef<HTMLDivElement>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  currentContext.current = { selected, baseRevision, canCommit }
  const session = sessions.find(s => s.id === selected)
  const streaming = session?.messages.at(-1)?.state === 'streaming'
  const selectionKey = `yeeeyee.chat.selected.${userId}.${new URLSearchParams(window.location.search).get('canvas') ?? 'default'}`
  async function refresh() {
    const list = await request<ChatSession[]>('/api/web/chat/sessions')
    if (mounted.current) {
      let changed = false
      for (const s of list) for (const m of s.messages) {
        if (m.committedRevision != null) {
          if (knownCommits.current.has(m.id) && knownCommits.current.get(m.id) !== m.committedRevision || !knownCommits.current.has(m.id) && m.actionState === 'committed') changed = true
          knownCommits.current.set(m.id, m.committedRevision)
        }
      }
      setSessions(list)
      if (changed) await onChanged()
    }
    return list
  }
  useEffect(() => {
    mounted.current = true
    setReady(false)
    void refresh().then(list => {
      if (!mounted.current) return
      const remembered = localStorage.getItem(selectionKey)
      setSelected(current => current || list.find(s => s.id === remembered)?.id || list[0]?.id || '')
      setReady(true)
    }).catch(e => { if (mounted.current) { setNotice(errorMessage(e)); setReady(true) } })
    return () => { mounted.current = false; active.current?.abort(); clearPreview() }
  }, [userId])
  const pendingGeneration = sessions.some(s => s.messages.some(m => m.skillState === 'submitted'))
  useEffect(() => {
    if ((!streaming && !pendingGeneration) || busy) return
    const timer = window.setInterval(() => { void refresh().catch(e => { if (mounted.current) setNotice(errorMessage(e)) }) }, 1000)
    return () => window.clearInterval(timer)
  }, [streaming, pendingGeneration, busy])
  useEffect(() => { if (selected) localStorage.setItem(selectionKey, selected) }, [selected, selectionKey])
  useEffect(() => { if (history.current) history.current.scrollTop = history.current.scrollHeight }, [session?.messages])
  function update(next: ChatSession) { setSessions(list => [next, ...list.filter(s => s.id !== next.id)]) }
  async function create() {
    setBusy(true); setNotice('')
    try { const next = await request<ChatSession>('/api/web/chat/sessions', { method: 'POST' }); update(next); setSelected(next.id); setAttachments([]) }
    catch (e) { setNotice(errorMessage(e)) } finally { setBusy(false) }
  }
  async function remove() {
    if (!selected) return
    setBusy(true)
    try {
      await request(`/api/web/chat/sessions/${selected}`, { method: 'DELETE' })
      const list = await refresh(); setSelected(list[0]?.id ?? ''); setAttachments([]); setText(''); localStorage.removeItem(selectionKey)
    } catch (e) { setNotice(errorMessage(e)) } finally { setBusy(false) }
  }
  async function send(retry = false, nodeRequest?: NodeAgentRequest) {
    if (inFlight.current || streaming || !canChat || (!nodeRequest && !retry && mode === 'auto' && !canCommit) || (!retry && !(nodeRequest?.prompt ?? text).trim())) return
    const sendBody = retry ? { retry: true } : {
      text: nodeRequest?.prompt ?? text, retry: false, mode: nodeRequest ? 'approval' : mode,
      chapterId: nodeRequest ? nodeRequest.chapterId : chapterId, nodeId: nodeRequest?.nodeId ?? nodeId,
      baseRevision: nodeRequest?.baseRevision ?? baseRevision, source: nodeRequest ? 'node-menu' : 'chat',
      targetNodeTitle: nodeRequest?.title, attachmentIds: nodeRequest ? [] : attachments.map(a => a.id)
    }
    inFlight.current = true
    clearPreview()
    setApprovalId('')
    setBusy(true); setNotice('')
    const controller = new AbortController(); active.current = controller
    try {
      let id = nodeRequest ? '' : selected
      if (!id) { const next = await request<ChatSession>('/api/web/chat/sessions', { method: 'POST', signal: controller.signal }); id = next.id; update(next); setSelected(id) }
      executionSession.current = id
      const response = await fetch(canvasUrl(`/api/web/chat/sessions/${id}/messages`), {
        method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(sendBody), signal: controller.signal
      })
      await readChatStream(response, event => {
        if (!mounted.current) return
        if (event.type === 'snapshot' || event.type === 'done') {
          update(event.data as ChatSession)
          if (event.type === 'snapshot' && !retry) { setText(''); setAttachments([]); if (nodeRequest) onNodeAgentConsumed?.(nodeRequest.id) }
          if (event.type === 'done' && (event.data as ChatSession).messages.at(-1)?.actionState === 'committed') void onChanged().catch(e => setNotice(errorMessage(e)))
        } else {
          setSessions(list => list.map(s => {
            if (s.id !== id) return s
            const messages = s.messages.map((m, index) => index !== s.messages.length - 1 ? m : {
              ...m,
              content: event.type === 'text' ? m.content + (event.data as { text: string }).text : m.content,
              thinking: event.type === 'thinking' ? m.thinking + (event.data as { text: string }).text : m.thinking,
              usage: event.type === 'usage' ? (event.data as { usage?: ChatMessage['usage'] }).usage ?? m.usage : m.usage,
              usageReport: event.type === 'usage' ? (event.data as { usageReport?: ChatMessage['usageReport'] }).usageReport ?? m.usageReport : m.usageReport,
              contextWindow: event.type === 'usage' ? (event.data as { contextWindow?: number }).contextWindow ?? m.contextWindow : m.contextWindow,
              firstTokenSeconds: event.type === 'usage' ? (event.data as { firstTokenSeconds?: number }).firstTokenSeconds ?? m.firstTokenSeconds : m.firstTokenSeconds,
              decodeSeconds: event.type === 'usage' ? (event.data as { decodeSeconds?: number }).decodeSeconds ?? m.decodeSeconds : m.decodeSeconds
            })
            return { ...s, messages }
          }))
        }
      })
    } catch (e) {
      if (nodeRequest) onNodeAgentConsumed?.(nodeRequest.id)
      if (mounted.current) setNotice(errorMessage(e))
    }
    finally { active.current = null; executionSession.current = ''; inFlight.current = false; if (mounted.current) { setBusy(false); void refresh().catch(e => setNotice(errorMessage(e))) } }
  }
  useEffect(() => {
    if (!ready || !nodeAgentRequest || consumedRequests.current.has(nodeAgentRequest.id) || busy || streaming || !canChat) return
    consumedRequests.current.add(nodeAgentRequest.id)
    void send(false, nodeAgentRequest)
  }, [nodeAgentRequest, ready, busy, streaming, canChat])
  async function cancel() {
    const id = executionSession.current || selected
    clearPreview()
    try { await request(`/api/web/chat/sessions/${id}/cancel`, { method: 'POST' }) }
    catch (e) { setNotice(errorMessage(e)); active.current?.abort() }
  }
  const pendingApprovals = session?.messages.filter(m => m.actionState === 'preview' && m.mode !== 'readonly' && !!m.actions?.length) ?? []
  const approval = pendingApprovals.find(m => m.id === approvalId) ?? pendingApprovals.at(-1)
  const approvalKey = approval ? chatPreviewKey(selected, approval, baseRevision) : ''
  useEffect(() => {
    clearPreview()
    attemptedPreview.current = ''
    setApprovalId('')
  }, [selected])
  useEffect(() => {
    clearPreview()
    if (approval && approval.baseRevision !== baseRevision) setNotice('画布修订已变化，旧审批不能提交；请重新生成或重新预检。')
  }, [baseRevision])
  // 待审批的动作集合变了（典型：生成任务完成后服务端向同一条回复追加 attach_asset，
  // 而消息 ID 与画布修订都没变）：手上那份预览批的是**上一版动作**，立刻收掉，
  // 画布上的虚影一并撤掉——留着它等于让人按一份看不见的动作去批准。
  // 随后 approvalKey 变化会让下面那条自动预检按最新动作重新跑一次（服务端也会再核一次）。
  useEffect(() => {
    if (!preview || !approval || preview.messageId !== approval.id) return
    if (preview.actions === chatActionsKey(approval)) return
    clearPreview()
    setNotice('审批动作已更新（生成产物已回挂到同一条回复），旧预览已失效，正在按最新动作重新预检。')
  }, [approvalKey])
  useEffect(() => {
    // 手上没有待审批对象：把「已经预检过谁」也一并清掉。这条审批**离开**过（提交、撤销、丢弃）
    // 之后再回来时，算是一份新的待审批，应当重新预检一次（撤销后恢复批准走的就是这条路径）。
    // 只在「没有待审批对象」时清，所以不会把上面那条“同一条审批只预检一次”的约束放松：
    // 预检请求进行中 approvalKey 一直有值。
    if (!approvalKey) { clearPreview(); attemptedPreview.current = ''; return }
    if (busy || streaming || attemptedPreview.current === approvalKey) return
    attemptedPreview.current = approvalKey
    void action(approval!.id, 'preview')
  }, [approvalKey, busy, streaming])
  function canApprove(message: ChatMessage) {
    return canCommitChatPreview(message, preview, selected, baseRevision, canCommit)
  }
  async function action(messageId: string, operation: string) {
    const sessionId = selected
    const message = session?.messages.find(m => m.id === messageId)
    if (inFlight.current || streaming || !message) return
    if (operation === 'commit' && !canApprove(message)) { setNotice('请先成功预检当前审批对象；画布修订变化后需要重新生成或预检。'); return }
    inFlight.current = true
    setBusy(true); setNotice('')
    clearPreview()
    const run = previewRun.current
    const controller = new AbortController()
    previewController.current = controller
    // 记下「我刚预检的是哪一份」——必须与上面 effect 用的是**同一个** chatPreviewKey，
    // 否则预检结束、busy 变回 false 让 effect 重跑时两边对不上，同一条审批会被反复预检。
    if (operation === 'preview') attemptedPreview.current = chatPreviewKey(sessionId, message, baseRevision)
    try {
      const result = await request(`/api/web/chat/sessions/${sessionId}/actions/${messageId}/${operation}`, { method: 'POST', signal: operation === 'preview' ? controller.signal : undefined })
      if (operation === 'preview') {
        const errors = (result as { errors?: string[] }).errors
        if (errors?.length) throw new Error(errors.join('；'))
        const parsed = parseAgentPreview(result)
        if (!mounted.current || run !== previewRun.current || currentContext.current.selected !== sessionId) return
        if (parsed.sessionId !== sessionId || parsed.messageId !== messageId) throw new Error('预检响应的审批对象不匹配')
        const list = await refresh()
        if (!mounted.current || run !== previewRun.current || currentContext.current.selected !== sessionId) return
        const checked = list.find(s => s.id === sessionId)?.messages.find(m => m.id === messageId)
        if (checked?.actionState !== 'preview' || checked.actionErrors?.length) throw new Error(checked?.actionErrors?.join('；') || '本条审批预检未通过')
        if (parsed.revision !== currentContext.current.baseRevision || checked.baseRevision !== parsed.revision) throw new Error('画布修订已变化，请重新生成或重新预检')
        // 一并记下**这一版动作**的身份：之后动作再变（追加 attach_asset 等），这份预览就作废了。
        setPreview({ ...parsed, actions: chatActionsKey(checked) })
        previewCallback.current?.(parsed)
      } else {
        await refresh()
        if (operation !== 'discard') await onChanged()
      }
    } catch (e) {
      // 这次失败只认它自己的上下文：会话换了（`selected` 变了）就什么都不做，
      // 也不认已经作废的那一次预检（`previewRun` 变了 = 期间又发起过别的预检）。
      // 审批消息由「发起时的地址 + 序号」绑住：请求打的是 `${sessionId}/…/${messageId}`，
      // 序号又是最新那一次，所以迟到回包不会算到别的审批头上。
      if (mounted.current && !controller.signal.aborted && currentContext.current.selected === sessionId && (operation !== 'preview' || run === previewRun.current)) {
        clearPreview(); setNotice(errorMessage(e))
        // 预检失败之后**必须把最新会话消息取回来**：这次校验的结论（哪些动作不通过、
        // 有没有留下旧的提示）都写在消息上——服务端写了、界面不刷新，就会继续显示上一次那份
        // 「可以批准」的样子。刷新期间 busy 还是 true，自动预检那条 effect 不会在半路上又发一次。
        if (operation === 'preview') {
          try { await refresh() }
          catch (failure) { setNotice(`${errorMessage(e)}；刷新会话失败：${errorMessage(failure)}`) }
        }
      }
    } finally {
      inFlight.current = false
      if (mounted.current) setBusy(false)
    }
  }
  const last = session?.messages.at(-1)
  const latestReply = session?.messages.slice().reverse().find(m => m.role === 'assistant')
  return <div className="df-agent-chat">
    <div className="df-agent-chat-toolbar">
      <div className="df-row">
      <select aria-label="切换会话" className="df-select" value={selected} disabled={busy} onChange={e => { setSelected(e.target.value); setNotice(''); setText(''); setAttachments([]) }}>
        <option value="">新对话</option>{sessions.map(s => <option key={s.id} value={s.id}>{s.title}</option>)}
      </select>
      <button className="df-mini-button" disabled={busy} onClick={() => void create()}>新建会话</button>
      <button className="df-mini-button" disabled={busy || streaming || !selected} onClick={() => void remove()}>删除</button>
      <button className="df-mini-button" disabled={busy} onClick={() => void refresh().catch(e => setNotice(errorMessage(e)))}>恢复</button>
    </div>
    <span className="df-agent-context" title={`上下文：章节 ${chapterId ?? '全部'} · 节点 ${nodeId ?? '未选择'} · 修订 ${baseRevision ?? '无项目'}`}>上下文：章节 {chapterId?.slice(0, 8) ?? '全部'} · 节点 {nodeId?.slice(0, 8) ?? '未选择'} · 修订 {baseRevision ?? '无项目'}</span>
    <label className="df-agent-authorization"><span>画布权限</span><select aria-label="画布执行模式" className="df-select" value={mode} disabled={busy || streaming} onChange={e => setMode(e.target.value)}><option value="readonly">只读讨论</option><option value="approval">预览后审批</option><option value="auto" disabled={!canCommit}>预检后自动提交</option></select></label>
    </div>
    <div ref={history} className="df-agent-history df-scroll">
    <div role="log" aria-label="对话消息" className="df-section">
      {!session?.messages.length && <div className="df-empty">发送消息开始对话。</div>}
      {session?.messages.map(m => <article key={m.id} className={`df-agent-message df-section ${m.role === 'user' ? 'is-user' : 'is-assistant'}`}>
        <span className="df-agent-message-role">{m.role === 'user' ? '你' : `Agent · ${m.model}`}</span>
        {m.thinking && <details className="df-agent-thinking"><summary>思考 · {m.thinking.length} 字<span className="df-agent-thinking-preview"><span>{m.thinking.trimEnd().slice(-600)}</span></span></summary><div style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{m.thinking}</div></details>}
        <div style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{(m.role === 'assistant' ? m.content.replace(/^[ \t]*```json[ \t]*\r?\n(?:[ \t]*\r?\n)*[ \t]*```[ \t]*(?=\r?$)/gim, '') : m.content) || (m.state === 'streaming' ? '等待模型回复…' : '')}</div>
        {m.attachmentIds?.map((attachmentId, index) => <a key={attachmentId} href={canvasUrl(`/api/web/chat/sessions/${session.id}/attachments/${attachmentId}`)} target="_blank" rel="noreferrer">查看附件 {index + 1}</a>)}
        {m.role === 'assistant' && <span className="df-dim">执行 {m.id} · {m.state}{m.actionState ? ` · ${m.actionState}` : ''}</span>}
        {m.targetNodeId && <span className="df-agent-context">来源节点：{m.targetNodeTitle || m.targetNodeId} · {m.targetNodeId} · 章节 {m.targetChapterId ?? '未归档'} · 基准修订 {m.baseRevision ?? '—'}{m.source ? ` · ${m.source}` : ''}</span>}
        {m.error && <span role="alert" className="df-notice is-error">{m.error}</span>}
        {m.ask && <div className="df-section"><strong>{m.ask.question}</strong>{m.ask.options.map(option => <button key={option} className="df-mini-button" disabled={busy || streaming} onClick={() => setText(option)}>{option}</button>)}<span className="df-dim">也可在输入框填写回答后发送。</span></div>}
        {['preview', 'failed'].includes(m.skillState ?? '') && m.skillActions?.length === 1 && ['web.text-to-image', 'comfyui.text-to-image'].includes(m.skillActions[0].target) && <div className="df-section">
          <span>生图前请选择比例 · {m.skillActions[0].target === 'web.text-to-image' ? '图片 API' : 'ComfyUI'} · {m.skillActions[0].referenceAttachmentIds?.length ? `使用 ${m.skillActions[0].referenceAttachmentIds.length} 张聊天参考图；实体必要参考仍会校验` : m.targetNodeId ? '实体参考图由服务端按本条消息的来源节点校验；实体必要参考仍会校验' : '参考图由服务端按当前上下文校验'}</span>
          <div className="df-row df-wrap">{['1:1', '16:9', '9:16', '4:3', '3:4'].map(ratio => <button key={ratio} className="df-mini-button" aria-pressed={imageRatios[m.id] === ratio} disabled={busy || streaming} onClick={() => setImageRatios(current => ({ ...current, [m.id]: ratio }))}>{ratio}</button>)}
            <button className="df-primary-button" disabled={busy || streaming || !canChat || !imageRatios[m.id]} onClick={() => void generateImage(m.id)}>按所选比例生成</button>
          </div>
        </div>}
        {m.skillState === 'submitted' && <span className="df-dim">生成请求已提交；完成后按本条对话的画布权限处理回挂。可继续对话。</span>}
        {m.skillError && <span role="alert" className="df-notice is-error">{m.skillError}</span>}
        {m.skillState === 'completed' && m.skillJobIds?.map(jobId => <GeneratedOutputs key={jobId} jobId={jobId} />)}
        {m.skillState === 'completed' && m.mode === 'readonly' && <span className="df-dim">生成完成；只读模式未写入画布。</span>}
        {m.mode !== 'readonly' && !!m.actions?.length && <div className="df-section df-agent-approval">
          <span>动作状态：{m.actionState} · {m.mode}</span>
          {m.actions.map((a, i) => <span key={i}>{a.kind} · {a.title || a.target} · {a.reason}</span>)}
          {m.hints?.filter(Boolean).map((h, i) => <span key={i} className="df-notice">{h}</span>)}
          {m.actionErrors?.map((h, i) => <span key={i} role="alert" className="df-notice is-error">{h}</span>)}
          <div className="df-row">
            <button className="df-mini-button" disabled={busy || streaming || !(m.actionState === 'preview' || canCommit && m.actionState === 'undone' && m.skillState === 'completed' && !!m.skillJobIds?.length && m.actions?.every(a => a.kind === 'attach_asset'))} onClick={() => { setApprovalId(m.id); void action(m.id, 'preview') }}>预检预览</button>
            <button className="df-primary-button" disabled={busy || streaming || !canApprove(m)} onClick={() => void action(m.id, 'commit')}>批准提交</button>
            <button className="df-mini-button" disabled={busy || streaming || m.actionState !== 'preview'} onClick={() => void action(m.id, 'discard')}>丢弃</button>
            <button className="df-mini-button" disabled={busy || streaming || !canCommit || m.actionState !== 'committed'} onClick={() => void action(m.id, 'undo')}>撤销提交</button>
          </div>
          {preview?.sessionId === selected && preview.messageId === m.id && <span className="df-notice">当前审批预检通过，差异虚影已显示在画布（修订 {preview.revision}），批准后才写入。</span>}
          {preview?.sessionId === selected && preview.messageId === m.id && (
            // 键按「会话 + 消息 + 修订」给：换审批、重新预检、修订变化都会整块重建，
            // 旧详情与旧媒体（以及它们的失败状态）不会留到下一批变更上。
            <PreviewAssetChanges key={`${preview.sessionId}:${preview.messageId}:${preview.revision}`} preview={preview} records={records} />
          )}
          {m.actionState === 'preview' && m.baseRevision !== baseRevision && <span className="df-notice is-error">基准修订已过期，请重新生成提议。</span>}
        </div>}
      </article>)}
    </div>
    </div>
    <div className="df-agent-composer-area">
    {notice && <span role="alert" className="df-notice is-error">{notice}</span>}
    {!canChat && <span className="df-notice">当前账号没有模型调用权限。</span>}
    {mode === 'auto' && !canCommit && <span role="alert" className="df-notice is-error">自动提交需要可编辑画布且没有未保存修改；请先保存，或切换到审批模式。</span>}
    <div className="df-agent-composer">
    <input ref={fileInput} hidden aria-label="聊天附件" type="file" accept="image/png,image/jpeg,text/plain,.txt" disabled={busy || streaming || !canChat} onChange={e => { const file = e.target.files?.[0]; if (file) void upload(file); e.target.value = '' }} />
    {!!attachments.length && <div className="df-row df-wrap">{attachments.map(a => <div key={a.id} className="df-agent-attachment"><a href={canvasUrl(`/api/web/chat/sessions/${selected}/attachments/${a.id}`)} target="_blank" rel="noreferrer">{a.name}</a><button className="df-mini-button" disabled={busy} onClick={() => setAttachments(current => current.filter(item => item.id !== a.id))}>移除</button></div>)}</div>}
    <textarea aria-label="聊天消息" className="df-input" maxLength={16000} rows={3} value={text} disabled={busy || !canChat} onChange={e => setText(e.target.value)} placeholder="和 Agent 讨论创作" onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); void send() } }} />
    <div className="df-agent-composer-actions">
      <button type="button" className="df-tool-button" title="添加PNG/JPEG（5MiB）或UTF-8文本（64KiB）" disabled={busy || streaming || !canChat} onClick={() => fileInput.current?.click()}>附件</button>
      {last && ['failed', 'cancelled', 'interrupted'].includes(last.state) && <button className="df-mini-button" disabled={busy || streaming || !canChat} onClick={() => void send(true)}>重试回复</button>}
      <div className="df-agent-submit">
        {streaming || active.current
          ? <button className="df-primary-button" disabled={!selected} onClick={() => void cancel()}>取消生成</button>
          : <button className="df-primary-button" disabled={busy || !canChat || (mode === 'auto' && !canCommit) || !text.trim()} onClick={() => void send()}>发送</button>}
      </div>
    </div>
    </div>
    {latestReply && <ChatUsageRow key={`${selected}:${latestReply.id}`} message={latestReply} />}
    <span className="df-agent-input-hint">Enter 发送 · Shift+Enter 换行</span>
    </div>
  </div>
}

/**
 * 待审批的附件 / 引用变化详情：把「到底会多出、少掉、改掉什么」在批准之前摊开。
 *
 * 只读两样东西：预检回来的差异投影（服务端算的），以及当前正式场景记录（变更前的那一份）。
 * **不写节点、不写实体库、不写画布**——它是展示，不是执行。
 *
 * 为什么单独成一层而不是塞进虚影卡：虚影整层是不可点的（点得中就等于把画布手势放给了它），
 * 而详情要能展开、要能点开大图。所以摘要留在虚影上，详情放在审批区（本来就在操作审批的地方）。
 */
export function PreviewAssetChanges({ preview, records }: { preview: AgentCanvasPreview; records: ViewRecord[] }) {
  const deltas = useMemo(() => agentPreviewAssetDiffs(preview, records), [preview, records])
  const items = deltas.reduce((sum, delta) => sum + delta.attachments.length + delta.references.length, 0)
  if (deltas.length === 0) return <span className="df-dim">本次审批没有附件或引用的变化。</span>
  return <details className="df-agent-preview-change-detail">
    <summary>附件 / 引用变更详情 · {deltas.length} 个节点 · {items} 项</summary>
    {deltas.map(delta => <div key={delta.recordId} className={`df-section df-preview-asset-node is-${delta.change}`}>
      <span className="df-row df-wrap" style={{ gap: 6 }}>
        <span className="df-preview-asset-change">{changeLabel(delta.change)}节点</span>
        <span className="df-node-title">{delta.title}</span>
      </span>
      {delta.attachments.map((item, index) => <PreviewAttachmentRow key={`${index}-${item.id}`}
        delta={item} recordId={delta.recordId} preview={preview} />)}
      {delta.references.map((item, index) => <PreviewReferenceRow key={`${index}-${item.key}`} delta={item} />)}
    </div>)}
    <span className="df-dim">只读展示：批准之前不写正式节点、实体库与画布。</span>
  </details>
}

/** 一条附件变化：名称、媒体类型、来源，加上（能读到时）那张图的缩略图或视频入口。 */
function PreviewAttachmentRow({ delta, recordId, preview }: { delta: AttachmentDelta; recordId: string; preview: AgentCanvasPreview }) {
  const item = delta.after ?? delta.before!
  // 变更前就在的产物走**正式**那条按 ID 取字节的接口（同一 ID 就是同一份字节）；
  // 只有新增的那一件还没进正式节点，得让服务端按本条消息这批动作重新试算后再取。
  const url = delta.before
    ? attachmentFileUrl(recordId, item.id)
    : previewAttachmentFileUrl(preview.sessionId, preview.messageId, recordId, item.id)
  return <span className="df-row df-wrap df-preview-asset-row">
    <span className="df-preview-asset-change">{changeLabel(delta.change)}附件</span>
    <span className="df-dim">{attachmentKindLabel(item.kind)}</span>
    <span>{item.name}</span>
    {item.source && <span className="df-dim">来源 {item.source}</span>}
    {delta.fields.map(field => <span key={field.label} className="df-dim">{field.label}：{field.before} → {field.after}</span>)}
    <PreviewAttachmentMedia url={url} attachment={item} />
  </span>
}

/**
 * 附件媒体的取字节结果。**读不到就只说读不到**：一行说明留在原地，上面的文字差异照旧——
 * 待提交的产物本来就可能因为文件没挂进这个部署而取不到，那不该让整块审批详情失败。
 */
function PreviewAttachmentMedia({ url, attachment }: { url: string; attachment: AttachmentSummary }) {
  const [failure, setFailure] = useState('')
  if (failure) return <span className="df-dim" role="status">媒体暂不可读取（{failure}）；文字差异仍然有效。</span>
  if (attachment.kind === 'Image')
    return <a href={url} target="_blank" rel="noreferrer" title={`查看 · ${attachment.name}`}>
      <img className="df-attachment-thumb" src={url} alt={attachment.name} loading="lazy"
        onError={() => setFailure('文件不在这个部署里，或这个地址已失效')} />
    </a>
  if (attachment.kind === 'Video')
    return <a href={url} target="_blank" rel="noreferrer">查看视频</a>
  return null
}

/** 一条引用变化：实体 · 变体 · 取媒体方式（锁定版本 / 跟随最新），加变了的字段。 */
function PreviewReferenceRow({ delta }: { delta: ReferenceDelta }) {
  const item = delta.after ?? delta.before!
  // 引用目标在正式画布里还解析得到时给一张缩略图；解析不到（或还没提交的版本）就不给图，只留文字。
  const thumb = item.variantId && !item.unresolved
    ? entityThumbUrl(item.entityId, item.variantId, item.variantVersionId ?? null)
    : ''
  return <span className="df-row df-wrap df-preview-asset-row">
    <span className="df-preview-asset-change">{changeLabel(delta.change)}引用</span>
    <span>{referenceLabel(item)}</span>
    <span className="df-dim">{referenceModeText(item)}</span>
    {delta.fields.map(field => <span key={field.label} className="df-dim">{field.label}：{field.before} → {field.after}</span>)}
    {thumb && <PreviewReferenceThumb url={thumb} label={referenceLabel(item)} />}
  </span>
}

function PreviewReferenceThumb({ url, label }: { url: string; label: string }) {
  const [failure, setFailure] = useState(false)
  if (failure) return null
  return <img className="df-attachment-thumb" src={url} alt={label} loading="lazy" onError={() => setFailure(true)} />
}
