/**
 * Agent 协作面板：本机技能与任务。
 *
 * 桌面端的 AgentPanel 是 1106 行，装着 13 个动作、导入向导、抽卡这些流程。
 * 网页端目前能拿到的是服务端**已预授权**的技能目录，以及任务队列的查询/取消/重试，
 * 所以这里只搬那三个真的能用的动作，其余动作等接口齐了再逐个补——
 * 摆一堆点了会 404 的按钮不算功能对等。
 */

import { useState } from 'react'
import { VIDEO_FRAMES_PER_SECOND, VIDEO_SIZE_OPTIONS, videoEffectiveSeconds, videoFramesForSeconds, type VideoParams } from './videoParams'

export type WebSkill = { id: string; name: string; capability: string }

export type WebJob = {
  jobId: string
  state: string
  progressPercent: number
  errorCode?: string | null
  errorMessage?: string
  attempt: number
  retryOfJobId?: string
  rootJobId: string
  canRetry: boolean
  outputs: { role: string; ref: string }[]
}

export type AgentPanelProps = {
  skills: WebSkill[]
  jobs: WebJob[]
  prompt: string
  videoParams: VideoParams
  busy: boolean
  canInvoke: boolean
  notice: string
  onPrompt: (value: string) => void
  onVideoParams: (next: VideoParams) => void
  onInvoke: (skillId: string) => void
  onCancel: (jobId: string) => void
  onRetry: (jobId: string) => void
}

const RUNNING_STATES = ['Queued', 'Running']

export function AgentPanel(props: AgentPanelProps) {
  const { skills, jobs, prompt, videoParams, busy, canInvoke, notice, onPrompt, onVideoParams, onInvoke, onCancel, onRetry } = props
  const [mediaErrors, setMediaErrors] = useState<Record<string, boolean>>({})
  return (
    <div className="df-section" style={{ gap: 16 }}>
      <div className="df-section">
        <div className="df-row-between">
          <span className="df-heading" style={{ fontSize: 12 }}>本机技能</span>
          <span className="df-dim" style={{ fontSize: 10 }}>{skills.length} 项已预授权</span>
        </div>
        <span className="df-notice">{notice}</span>
        {skills.length === 0 && <span className="df-notice">服务端还没有预授权任何本机技能；任务仍可只读查看。</span>}
        {skills.map((skill) => (
          <div key={skill.id} className="df-card df-section" style={{ gap: 8 }}>
            <div className="df-row-between">
              <span className="df-heading" style={{ fontSize: 12 }}>{skill.name}</span>
              <span className="df-dim df-mono" style={{ fontSize: 10 }}>{skill.capability}</span>
            </div>
            <span className="df-dim df-mono" style={{ fontSize: 10, wordBreak: 'break-all' }}>{skill.id}</span>
            <div className="df-field is-boxed">
              <textarea
                className="df-input"
                style={{ minHeight: 56 }}
                maxLength={4000}
                value={prompt}
                placeholder="给这个技能一段提示词"
                onChange={(event) => onPrompt(event.target.value)}
              />
            </div>
            {skill.id === 'web.text-to-video' && (
              <div className="df-section" style={{ gap: 6 }}>
                <span className="df-dim" style={{ fontSize: 10 }}>画面参数</span>
                <div className="df-row" style={{ gap: 6, flexWrap: 'wrap' }}>
                  <label className="df-dim" style={{ fontSize: 10 }}>
                    时长（秒）
                    <input
                      className="df-input"
                      style={{ width: 56 }}
                      inputMode="numeric"
                      value={videoParams.seconds}
                      onChange={(event) => onVideoParams({ ...videoParams, seconds: event.target.value })}
                    />
                  </label>
                  <label className="df-dim" style={{ fontSize: 10 }}>
                    边长
                    <select
                      className="df-input"
                      value={videoParams.size}
                      onChange={(event) => onVideoParams({ ...videoParams, size: event.target.value })}
                    >
                      {VIDEO_SIZE_OPTIONS.map((option) => (
                        <option key={option} value={option}>{option}×{option}</option>
                      ))}
                    </select>
                  </label>
                  <label className="df-dim" style={{ fontSize: 10 }}>
                    种子（留空随机）
                    <input
                      className="df-input"
                      style={{ width: 96 }}
                      inputMode="numeric"
                      value={videoParams.seed}
                      onChange={(event) => onVideoParams({ ...videoParams, seed: event.target.value })}
                    />
                  </label>
                </div>
                <span className="df-dim" style={{ fontSize: 10 }}>
                  这次 {videoFramesForSeconds(videoParams.seconds)} 帧 ≈ {videoEffectiveSeconds(videoParams.seconds).toFixed(2)} 秒（{VIDEO_FRAMES_PER_SECOND}fps，帧数按 1+8k 向上取整）；边长越大越慢、越吃显存。
                </span>
              </div>
            )}
            <button
              type="button"
              className="df-mini-button"
              style={{ alignSelf: 'flex-start' }}
              disabled={busy || !canInvoke || prompt.trim().length === 0}
              title={canInvoke ? '调用这个技能' : '当前账号没有 skill.invoke 权限'}
              onClick={() => onInvoke(skill.id)}
            >调用技能</button>
          </div>
        ))}
      </div>

      <hr className="df-divider" />

      <div className="df-section">
        <div className="df-row-between">
          <span className="df-heading" style={{ fontSize: 12 }}>任务</span>
          <span className="df-dim" style={{ fontSize: 10 }}>{jobs.length} 条</span>
        </div>
        {jobs.length === 0 && <span className="df-notice">还没有任务。</span>}
        {jobs.map((job) => (
          <div key={job.jobId} className="df-card df-section" style={{ gap: 6 }}>
            <div className="df-row-between">
              <span className="df-mono" style={{ fontSize: 11 }}>{job.jobId}</span>
              <span className="df-muted" style={{ fontSize: 11 }}>{job.state} · {job.progressPercent}%</span>
            </div>
            <span className="df-dim" style={{ fontSize: 10 }}>第 {job.attempt} 次{retryNote(job)}</span>
            {job.errorCode && <span className="df-notice is-error">{job.errorCode}：{job.errorMessage || '任务失败'}</span>}
            {job.outputs?.map((output, index) => {
              const outputUrl = `/api/web/jobs/${job.jobId}/outputs/${index}`
              if (output.role === 'video') {
                return (
                  <div key={index} className="df-section" style={{ gap: 6 }}>
                    <span className="df-dim" style={{ fontSize: 10 }}>视频产物</span>
                    {mediaErrors[outputUrl] ? (
                      <span className="df-notice is-error">视频加载失败，请重试或下载文件检查。</span>
                    ) : (
                      <video
                        controls
                        preload="metadata"
                        style={{ width: '100%', maxHeight: 220, background: '#111' }}
                        src={outputUrl}
                        onError={() => setMediaErrors((current) => ({ ...current, [outputUrl]: true }))}
                      />
                    )}
                    <a className="df-mini-button" href={outputUrl} download>
                      下载视频
                    </a>
                  </div>
                )
              }
              return (
                <span key={index} className="df-dim df-mono" style={{ fontSize: 10, wordBreak: 'break-all' }}>
                  {output.role}：{output.ref}
                </span>
              )
            })}
            <div className="df-row" style={{ gap: 6 }}>
              <button
                type="button"
                className="df-mini-button"
                disabled={busy || !RUNNING_STATES.includes(job.state)}
                onClick={() => onCancel(job.jobId)}
              >取消</button>
              <button
                type="button"
                className="df-mini-button"
                disabled={busy || !job.canRetry}
                onClick={() => onRetry(job.jobId)}
              >重试</button>
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}

function retryNote(job: WebJob): string {
  return job.retryOfJobId ? ` · 重试自 ${job.retryOfJobId}` : ''
}
