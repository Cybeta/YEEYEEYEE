import { useEffect, useRef, useState } from 'react'
import { canvasUrl, errorMessage, request } from '../api'
import { readChatStream, type ChatSession, type ChatUsage } from './chatStream'

import type { WebJob, WebSkill } from './AgentPanel'

export function ChatPanel({ userId, canChat, chapterId, nodeId, baseRevision, canCommit, onChanged, skills, jobs, onInvoke, onCancelJob, onRetryJob }: {
  userId: string; canChat: boolean; chapterId?: string; nodeId?: string; baseRevision?: number; canCommit: boolean;
  onChanged: () => Promise<void>; skills: WebSkill[]; jobs: WebJob[];
  onInvoke: (skillId: string, prompt: string) => Promise<WebJob>; onCancelJob: (id: string) => void; onRetryJob: (id: string) => Promise<WebJob>
}) {
  const [mode, setMode] = useState('approval')
  const [pendingLink, setPendingLink] = useState<{ sessionId: string; jobId: string } | null>(null)
  const [preview, setPreview] = useState<Record<string, unknown>>({})
  const [sessions, setSessions] = useState<ChatSession[]>([])
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
  const end = useRef<HTMLDivElement>(null)
  const session = sessions.find(s => s.id === selected)
  const streaming = session?.messages.at(-1)?.state === 'streaming'
  const selectionKey = `yeeeyee.chat.selected.${userId}.${new URLSearchParams(window.location.search).get('canvas') ?? 'default'}`
  async function refresh() {
    const list = await request<ChatSession[]>('/api/web/chat/sessions')
    if (mounted.current) setSessions(list)
    return list
  }
  useEffect(() => {
    mounted.current = true
    void refresh().then(list => {
      if (!mounted.current) return
      const remembered = localStorage.getItem(selectionKey)
      setSelected(list.find(s => s.id === remembered)?.id ?? list[0]?.id ?? '')
    }).catch(e => { if (mounted.current) setNotice(errorMessage(e)) })
    return () => { mounted.current = false; active.current?.abort() }
  }, [userId])
  useEffect(() => {
    if (!streaming || busy) return
    const timer = window.setInterval(() => { void refresh().catch(e => { if (mounted.current) setNotice(errorMessage(e)) }) }, 1000)
    return () => window.clearInterval(timer)
  }, [streaming, busy])
  useEffect(() => { if (selected) localStorage.setItem(selectionKey, selected) }, [selected, selectionKey])
  useEffect(() => { end.current?.scrollIntoView({ block: 'nearest' }) }, [session?.messages])
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
  async function send(retry = false) {
    if (busy || streaming || !canChat || (mode === 'auto' && !canCommit) || (!retry && !text.trim())) return
    setBusy(true); setNotice('')
    const controller = new AbortController(); active.current = controller
    try {
      let id = selected
      if (!id) { const next = await request<ChatSession>('/api/web/chat/sessions', { method: 'POST', signal: controller.signal }); id = next.id; update(next); setSelected(id) }
      const response = await fetch(canvasUrl(`/api/web/chat/sessions/${id}/messages`), {
        method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text, retry, mode, chapterId, nodeId, baseRevision, attachmentIds: attachments.map(a => a.id) }), signal: controller.signal
      })
      await readChatStream(response, event => {
        if (!mounted.current) return
        if (event.type === 'snapshot' || event.type === 'done') {
          update(event.data as ChatSession)
          if (event.type === 'snapshot' && !retry) { setText(''); setAttachments([]) }
          if (event.type === 'done' && (event.data as ChatSession).messages.at(-1)?.actionState === 'committed') void onChanged().catch(e => setNotice(errorMessage(e)))
        } else {
          setSessions(list => list.map(s => {
            if (s.id !== id) return s
            const messages = s.messages.map((m, index) => index !== s.messages.length - 1 ? m : {
              ...m,
              content: event.type === 'text' ? m.content + (event.data as { text: string }).text : m.content,
              thinking: event.type === 'thinking' ? m.thinking + (event.data as { text: string }).text : m.thinking,
              usage: event.type === 'usage' ? event.data as ChatUsage : m.usage
            })
            return { ...s, messages }
          }))
        }
      })
    } catch (e) { if (mounted.current) setNotice(errorMessage(e)) }
    finally { active.current = null; if (mounted.current) { setBusy(false); void refresh().catch(e => setNotice(errorMessage(e))) } }
  }
  async function cancel() {
    try { await request(`/api/web/chat/sessions/${selected}/cancel`, { method: 'POST' }) }
    catch (e) { setNotice(errorMessage(e)); active.current?.abort() }
  }
  async function action(messageId: string, operation: string) {
    setBusy(true); setNotice('')
    try {
      const result = await request(`/api/web/chat/sessions/${selected}/actions/${messageId}/${operation}`, { method: 'POST' })
      if (operation === 'preview') setPreview(p => ({ ...p, [messageId]: result }))
      else {
        setPreview(p => { const next = { ...p }; delete next[messageId]; return next })
        await refresh(); if (operation !== 'discard') await onChanged()
      }
    } catch (e) { setNotice(errorMessage(e)) } finally { setBusy(false) }
  }
  async function skill(skillId: string, retryId?: string) {
    if (busy || streaming) return
    setBusy(true); setNotice('')
    try {
      let id = selected
      if (!id) { const next = await request<ChatSession>('/api/web/chat/sessions', { method: 'POST' }); id = next.id; update(next); setSelected(id) }
      const images = attachments.filter(a => a.type.startsWith('image/'))
      const job = retryId ? await onRetryJob(retryId) : images.length
        ? await request<WebJob>(`/api/web/skills/${encodeURIComponent(skillId)}/invoke`, { method: 'POST', body: JSON.stringify({ prompt: text.trim(), idempotencyKey: crypto.randomUUID(), ...(nodeId ? { targetNodeId: nodeId } : {}), chatSessionId: id, referenceAttachmentIds: images.map(a => a.id) }) })
        : await onInvoke(skillId, text.trim())
      setPendingLink({ sessionId: id, jobId: job.jobId })
      try {
        update(await request<ChatSession>(`/api/web/chat/sessions/${id}/jobs/${job.jobId}`, { method: 'POST' }))
        setPendingLink(null); setNotice(`任务 ${job.jobId}：${job.state}`)
      } catch (e) { setNotice(`任务 ${job.jobId} 已创建，关联会话失败：${errorMessage(e)}。请重试关联，勿重复发起任务。`) }
    } catch (e) { setNotice(errorMessage(e)) } finally { setBusy(false) }
  }
  async function linkJob(sessionId: string, jobId: string) {
    setBusy(true)
    try { update(await request<ChatSession>(`/api/web/chat/sessions/${sessionId}/jobs/${jobId}`, { method: 'POST' })); setPendingLink(null); setNotice(`任务 ${jobId} 已关联`) }
    catch (e) { setNotice(`任务 ${jobId} 关联失败：${errorMessage(e)}`) } finally { setBusy(false) }
  }
  async function approveSkill(messageId: string) {
    setBusy(true); setNotice('')
    try { const job = await request<WebJob>(`/api/web/chat/sessions/${selected}/skills/${messageId}/invoke`, { method: 'POST' }); setNotice(`任务 ${job.jobId}：${job.state}（提交不等于生成完成）`) }
    catch (e) { setNotice(errorMessage(e)) }
    finally { await refresh().catch(e => setNotice(errorMessage(e))); setBusy(false) }
  }
  const last = session?.messages.at(-1)
  return <div className="df-section" style={{ gap: 10 }}>
    <div className="df-row-between"><span className="df-heading">Agent 对话</span><button className="df-mini-button" disabled={busy} onClick={() => void create()}>新建会话</button></div>
    <div className="df-row">
      <select aria-label="切换会话" className="df-input" value={selected} disabled={busy} onChange={e => { setSelected(e.target.value); setNotice(''); setText(''); setAttachments([]) }}>
        <option value="">新对话</option>{sessions.map(s => <option key={s.id} value={s.id}>{s.title}</option>)}
      </select>
      <button className="df-mini-button" disabled={busy || streaming || !selected} onClick={() => void remove()}>删除</button>
      <button className="df-mini-button" disabled={busy} onClick={() => void refresh().catch(e => setNotice(errorMessage(e)))}>恢复</button>
    </div>
    <span className="df-dim">上下文：章节 {chapterId ?? '全部'} · 节点 {nodeId ?? '未选择'} · 修订 {baseRevision ?? '无项目'}</span>
    <select aria-label="执行模式" className="df-input" value={mode} disabled={busy || streaming} onChange={e => setMode(e.target.value)}><option value="readonly">只读讨论</option><option value="approval">预览后审批</option><option value="auto" disabled={!canCommit}>预检后自动提交</option></select>
    <div role="log" aria-label="对话消息" className="df-section" style={{ maxHeight: 420, overflowY: 'auto' }}>
      {!session?.messages.length && <span className="df-notice">发送消息开始对话。</span>}
      {session?.messages.map(m => <article key={m.id} className="df-card df-section" style={{ gap: 6 }}>
        <span className="df-heading">{m.role === 'user' ? '你' : `Agent · ${m.model}`}</span>
        {m.thinking && <details><summary>思考</summary><div style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{m.thinking}</div></details>}
        <div style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{m.content || (m.state === 'streaming' ? '等待模型回复…' : '')}</div>
        {m.attachmentIds?.map((attachmentId, index) => <a key={attachmentId} href={canvasUrl(`/api/web/chat/sessions/${session.id}/attachments/${attachmentId}`)} target="_blank" rel="noreferrer">查看附件 {index + 1}</a>)}
        {m.usage && <span className="df-dim">输入 {m.usage.inputTokens} · 输出 {m.usage.completionTokens} · 缓存命中 {m.usage.cacheHitTokens} / 未命中 {m.usage.cacheMissTokens}</span>}
        {m.role === 'assistant' && <span className="df-dim">{m.state}</span>}
        {m.error && <span role="alert" className="df-notice is-error">{m.error}</span>}
        {m.ask && <div className="df-section"><strong>{m.ask.question}</strong>{m.ask.options.map(option => <button key={option} className="df-mini-button" disabled={busy || streaming} onClick={() => setText(option)}>{option}</button>)}<span className="df-dim">也可在输入框填写回答后发送。</span></div>}
        {!!m.skillActions?.length && <div className="df-section">
          <span>技能提议：{m.skillState}（submitted 仅表示任务已提交）</span>
          {m.skillActions.map((a, i) => <span key={i}>{a.target} · {a.content} · {a.reason}</span>)}
          {m.skillError && <span role="alert" className="df-notice is-error">{m.skillError}</span>}
          {m.skillJobIds?.map(id => <span key={id}>任务 {id}</span>)}
          <button className="df-mini-button" disabled={busy || streaming || !canChat || m.mode === 'readonly' || !['preview', 'failed'].includes(m.skillState ?? '')} onClick={() => void approveSkill(m.id)}>批准发起技能任务</button>
        </div>}
        {!!m.actions?.length && <div className="df-section">
          <span>动作状态：{m.actionState} · {m.mode}</span>
          {m.actions.map((a, i) => <span key={i}>{a.kind} · {a.title || a.target} · {a.reason}</span>)}
          {m.hints?.filter(Boolean).map((h, i) => <span key={i} className="df-notice">{h}</span>)}
          {m.actionErrors?.map((h, i) => <span key={i} role="alert" className="df-notice is-error">{h}</span>)}
          <div className="df-row">
            <button className="df-mini-button" disabled={busy || streaming || m.actionState !== 'preview'} onClick={() => void action(m.id, 'preview')}>预检预览</button>
            <button className="df-mini-button" disabled={busy || streaming || !canCommit || m.mode === 'readonly' || m.actionState !== 'preview' || !!m.actionErrors?.length} onClick={() => void action(m.id, 'commit')}>批准提交</button>
            <button className="df-mini-button" disabled={busy || streaming || m.actionState !== 'preview'} onClick={() => void action(m.id, 'discard')}>丢弃</button>
            <button className="df-mini-button" disabled={busy || streaming || !canCommit || m.actionState !== 'committed'} onClick={() => void action(m.id, 'undo')}>撤销提交</button>
          </div>
          {!!preview[m.id] && <details open><summary>提交前后快照（尚未提交）</summary><pre style={{ maxHeight: 260, overflow: 'auto' }}>{JSON.stringify(preview[m.id], null, 2)}</pre></details>}
        </div>}
      </article>)}<div ref={end} />
    </div>
    <div className="df-section">
      <span className="df-heading">会话技能任务</span>
      {pendingLink && <button className="df-mini-button" disabled={busy || streaming} onClick={() => void linkJob(pendingLink.sessionId, pendingLink.jobId)}>重试关联任务 {pendingLink.jobId}</button>}
      {selected && jobs.filter(j => !session?.jobIds?.includes(j.jobId)).map(j => <button key={j.jobId} className="df-mini-button" disabled={busy || streaming} onClick={() => void linkJob(selected, j.jobId)}>关联已有任务 {j.jobId} · {j.state}</button>)}
      {skills.map(s => <button key={s.id} className="df-mini-button" disabled={busy || streaming || !canChat || !text.trim() || mode === 'readonly'} onClick={() => void skill(s.id)}>用输入框提示词调用 {s.name}</button>)}
      {jobs.filter(j => session?.jobIds?.includes(j.jobId)).map(j => <div key={j.jobId} className="df-card df-section">
        <span>{j.jobId} · {j.state} · {j.progressPercent}% · 第 {j.attempt} 次</span>
        {j.errorMessage && <span role="alert">{j.errorMessage}</span>}
        {j.state === 'Succeeded' && j.outputs.map((o, i) => <a key={i} href={canvasUrl(`/api/web/jobs/${j.jobId}/outputs/${i}`)} target="_blank" rel="noreferrer">查看产物 {o.role}</a>)}
        <button className="df-mini-button" disabled={busy || !['Queued', 'Running'].includes(j.state)} onClick={() => onCancelJob(j.jobId)}>取消任务</button>
        <button className="df-mini-button" disabled={busy || streaming || !j.canRetry || mode === 'readonly'} onClick={() => void skill('', j.jobId)}>重试任务</button>
      </div>)}
    </div>
    {notice && <span role="alert" className="df-notice is-error">{notice}</span>}
    {!canChat && <span className="df-notice">当前账号没有模型调用权限。</span>}
    {mode === 'auto' && !canCommit && <span role="alert" className="df-notice">自动提交需要可编辑画布且没有未保存修改；请先保存，或切换到审批模式。</span>}
    <label className="df-dim">添加PNG/JPEG（5MiB）或UTF-8文本（64KiB）<input aria-label="聊天附件" type="file" accept="image/png,image/jpeg,text/plain,.txt" disabled={busy || streaming || !canChat} onChange={e => { const file = e.target.files?.[0]; if (file) void upload(file); e.target.value = '' }} /></label>
    {attachments.map(a => <div key={a.id} className="df-row"><a href={canvasUrl(`/api/web/chat/sessions/${selected}/attachments/${a.id}`)} target="_blank" rel="noreferrer">{a.name}</a><button disabled={busy} onClick={() => setAttachments(current => current.filter(item => item.id !== a.id))}>移除</button></div>)}
    <textarea aria-label="聊天消息" className="df-input" maxLength={16000} rows={3} value={text} disabled={busy || !canChat} onChange={e => setText(e.target.value)} placeholder="和 Agent 讨论创作" onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); void send() } }} />
    <div className="df-row">
      <button className="df-mini-button" disabled={busy || streaming || !canChat || (mode === 'auto' && !canCommit) || !text.trim()} onClick={() => void send()}>发送</button>
      <button className="df-mini-button" disabled={(!busy && !streaming) || !selected} onClick={() => void cancel()}>取消生成</button>
      <button className="df-mini-button" disabled={busy || !canChat || (mode === 'auto' && !canCommit) || !last || !['failed', 'cancelled', 'interrupted'].includes(last.state)} onClick={() => void send(true)}>重试回复</button>
    </div>
  </div>
}
