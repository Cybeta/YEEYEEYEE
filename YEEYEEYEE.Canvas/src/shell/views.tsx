import { useState } from 'react'
import { recordAttachments } from '../assets'
import { attachmentFileUrl } from './assetImages'
import {
  attachmentKindLabel, jobFailureReason, jobRows, jobStateHex, jobStateLabel, orphanJobs,
  type JobOutputAttachment, type JobRow, type NodeJob
} from './nodeMedia'
import { NODE_KINDS, kindOf, nodeStatusOf, recordStatus, recordTitle, type ChapterGroup, type ScriptEntry, type ViewRecord } from './records'

/**
 * 时间轴与剧本两个视图。
 *
 * 桌面端这两个视图是把当前画布的真实数据摊开（时间轴读章节/分镜/工作树项/成品，
 * 剧本把章节与分镜里的文字按顺序铺开），所以这里也一律读真实记录，不放占位数字。
 * 分组与排序规则在 records.ts 里（纯函数、有回归钉着），这两个组件只负责摆。
 *
 * 时间轴还多一件事：**生成任务**。它是节点之外的实时信息（服务端只在产物回挂成功时写节点状态），
 * 而「任务归谁」看任务自己的 `targetNodeId`——与当前选中项无关，也与画布卡片的判据同一份
 * （见 nodeMedia）：同一节点上并行几个任务就显示几行，任务和产物谁也不覆盖谁。
 */

export type TimelineViewProps = {
  groups: ChapterGroup[]
  activeChapter: string
  selectedId: string
  /** 当前画布上的生成任务（`GET /api/web/jobs`）。 */
  jobs?: NodeJob[]
  /**
   * 画布上**全部**可编辑节点的 recordId，不只是阶段/搜索筛出来的那些。
   * 用它区分「目标节点被删了」与「只是被筛掉了」——后者不该被报成不可用。
   */
  nodeIds?: string[]
  onChapter: (chapterId: string) => void
  onSelect: (recordId: string) => void
}

export function TimelineView({ groups, activeChapter, selectedId, jobs, nodeIds, onChapter, onSelect }: TimelineViewProps) {
  // 目标节点已经不在了的任务另起一条泳道说清楚。**不能**把它们塞进任何一张节点卡里：
  // 那就是「任务串节点」——用户会以为某个还活着的节点在跑别人的任务。
  const orphans = orphanJobs(jobs, nodeIds ?? [])

  if (groups.length === 0 && orphans.length === 0)
    return <div className="df-empty">当前画布没有可显示的章节或节点。</div>

  return (
    <div className="df-scroll-pad df-scroll">
      {groups.map((group) => (
        <section key={group.id || 'unfiled'} className="df-lane" style={activeChapter === group.id ? { borderColor: 'var(--df-line-glow)' } : undefined}>
          <div className="df-lane-head">
            <button
              type="button"
              className="df-text-button"
              style={{ color: activeChapter === group.id ? 'var(--df-ink)' : undefined, fontWeight: activeChapter === group.id ? 600 : 400 }}
              onClick={() => onChapter(group.id)}
              title="只看这一章"
            >
              {group.label}
            </button>
            <span className="df-dim">{group.records.length} 个节点</span>
          </div>
          {group.records.length === 0
            ? <div className="df-notice">这一章还没有节点。</div>
            : (
              <div className="df-lane-strip df-scroll">
                {group.records.map((record) => (
                  <TimelineCard
                    key={record.recordId}
                    record={record}
                    selected={record.recordId === selectedId}
                    jobs={jobs}
                    onSelect={onSelect}
                  />
                ))}
              </div>
            )}
        </section>
      ))}

      {orphans.length > 0 && (
        <section className="df-lane is-unavailable">
          <div className="df-lane-head">
            <span className="df-heading" style={{ fontSize: 13 }}>任务目标不可用</span>
            <span className="df-dim">{orphans.length} 个任务</span>
          </div>
          <div className="df-lane-strip df-scroll">
            {orphans.map((job) => {
              const failure = jobFailureReason(job)
              return (
                <div key={job.jobId} className="df-lane-chip is-unavailable">
                  <span className="df-node-kind">
                    <span className="df-node-dot" style={{ background: jobStateHex(job.state) }} aria-hidden="true" />
                    <span>{jobStateLabel(job.state)}</span>
                  </span>
                  <span className="df-dim" style={{ display: 'block', marginTop: 4 }}>
                    目标节点已删除或不可用：产物不会出现在任何节点上，也不会改挂到别的节点。
                  </span>
                  <span className="df-dim df-mono" style={{ fontSize: 10, wordBreak: 'break-all' }}>{job.targetNodeId}</span>
                  {failure.length > 0 && <span className="df-lane-task-fail">{failure}</span>}
                </div>
              )
            })}
          </div>
        </section>
      )}
    </div>
  )
}

/** 一张节点卡：节点本身 + 它身上每个任务各一行。 */
function TimelineCard({ record, selected, jobs, onSelect }: {
  record: ViewRecord
  selected: boolean
  jobs?: NodeJob[]
  onSelect: (recordId: string) => void
}) {
  const kind = kindOf(record.recordType)
  // 任务按 targetNodeId 归到这张卡上；产物只认「任务成功 + 服务端说已回挂 + 身份对得上」的那几件。
  const rows = jobRows(jobs, record.recordId, recordAttachments(record))
  return (
    <div className={`df-lane-chip${selected ? ' is-selected' : ''}`}>
      {/* 节点那一块是按钮，任务行不是：任务行里还有产物要能单独点，嵌套按钮在 HTML 里是无效的。 */}
      <button type="button" className="df-lane-node" onClick={() => onSelect(record.recordId)} title="在画布上选中这个节点">
        <span className="df-node-kind">
          <span className="df-node-dot" style={{ background: NODE_KINDS[kind].hex }} aria-hidden="true" />
          <span>{NODE_KINDS[kind].label}</span>
        </span>
        <span className="df-node-title df-ellipsis" style={{ display: 'block', marginTop: 4 }}>{recordTitle(record)}</span>
        <span className="df-dim df-mono" style={{ fontSize: 10 }}>{nodeStatusOf(recordStatus(record)).label}</span>
      </button>
      {rows.length > 0 && (
        <div className="df-lane-tasks">
          {rows.map((row) => <JobTaskRow key={row.jobId} row={row} recordId={record.recordId} onSelect={onSelect} />)}
        </div>
      )}
    </div>
  )
}

/** 一行任务：状态、进度、回挂中、失败原因，以及已经确认挂上来的产物。 */
function JobTaskRow({ row, recordId, onSelect }: {
  row: JobRow
  recordId: string
  onSelect: (recordId: string) => void
}) {
  // 点任务行 = 定位到它自己的目标节点（这里的 recordId 就是任务的 targetNodeId）。
  const title = `任务 ${row.jobId} · ${row.label}${row.failure.length > 0 ? `：${row.failure}` : ''}`
  return (
    <div className="df-lane-task">
      <button type="button" className="df-lane-task-head" title={title} onClick={() => onSelect(recordId)}>
        <span className="df-node-dot" style={{ background: row.hex }} aria-hidden="true" />
        <span>{row.label}</span>
        {/* 进度只对还在跑的任务说；拿不到有效进度就说「处理中」，不编一个百分比出来。 */}
        {row.active && (row.progress !== null
          ? <span className="df-dim df-mono">{Math.round(row.progress)}%</span>
          : <span className="df-dim">处理中…</span>)}
      </button>
      {row.active && row.progress !== null && (
        <span
          className="df-lane-task-bar"
          role="progressbar"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={Math.round(row.progress)}
        >
          <span style={{ width: `${row.progress}%` }} />
        </span>
      )}
      {/* 「任务成功了」与「节点上真多了这件产物」之间还有一段：这段里要说清在等回挂，
          否则用户看到「已完成」却没有图，只会以为界面卡住了。 */}
      {row.pending && <span className="df-dim" style={{ fontSize: 10 }}>产物还在回挂中…</span>}
      {row.failure.length > 0 && <span className="df-lane-task-fail" title={row.failure}>{row.failure}</span>}
      {row.outputs.map((output) => (
        <JobOutputTile key={output.attachmentId} recordId={recordId} output={output} onSelect={onSelect} />
      ))}
    </div>
  )
}

/**
 * 一件确认属于这个任务的产物。
 *
 * 图片给缩略图（点它选中目标节点，看大图仍在检查器里）；视频等只给「种类 · 名称」——
 * 时间轴上不摆播放器：那是节点主预览的活，摆在这里会同时抓元数据、还占掉整条泳道的高度。
 * 图读不到时**摘要与名称留着**，另加一句原因，也不把整条时间轴带崩。
 */
function JobOutputTile({ recordId, output, onSelect }: {
  recordId: string
  output: JobOutputAttachment
  onSelect: (recordId: string) => void
}) {
  const label = `${attachmentKindLabel(output.kind)} · ${output.name}`
  const [broken, setBroken] = useState(false)

  if (output.kind !== 'Image')
    return (
      <button type="button" className="df-lane-media" title={`${label} · 点它选中这个节点`} onClick={() => onSelect(recordId)}>
        <span className="df-ellipsis">{label}</span>
      </button>
    )

  return (
    <button
      type="button"
      className="df-lane-media"
      title={`${label}${broken ? ' · 图片无法读取' : ' · 点它选中这个节点'}`}
      onClick={() => onSelect(recordId)}
    >
      {broken
        ? <span className="df-lane-media-thumb is-empty" aria-hidden="true">无图</span>
        : <img
          className="df-lane-media-thumb"
          src={attachmentFileUrl(recordId, output.attachmentId)}
          alt={output.name}
          loading="lazy"
          onError={() => setBroken(true)}
        />}
      <span className="df-ellipsis">{label}</span>
      {broken && <span className="df-dim">图片无法读取</span>}
    </button>
  )
}

export type ScriptViewProps = {
  entries: ScriptEntry[]
  selectedId: string
  onSelect: (recordId: string) => void
}

export function ScriptView({ entries, selectedId, onSelect }: ScriptViewProps) {
  if (entries.length === 0) return <div className="df-empty">当前画布没有可摊开的章节或文字。</div>
  return (
    <div className="df-scroll-pad df-scroll">
      {entries.map((entry) => entry.kind === 'chapter'
        ? (
          <div key={entry.id}>
            <div className="df-script-chapter">
              <span className="df-node-dot" style={{ background: entry.hex }} aria-hidden="true" />
              <span className="df-heading" style={{ fontSize: 13 }}>{entry.title}</span>
              <span className="df-dim">{entry.meta}</span>
            </div>
            {/* 章节自己那段说明文字挂在章节条目上，不另占一张卡 */}
            {entry.body && <p className="df-script-body">{entry.body}</p>}
          </div>
        )
        : (
          <article key={entry.id} className="df-script-entry" style={selectedId === entry.id ? { borderLeftColor: 'var(--df-primary)' } : undefined}>
            <div className="df-row" style={{ gap: 8 }}>
              <button type="button" className="df-text-button" onClick={() => onSelect(entry.id)} title="在画布上选中这个节点">
                <span className="df-node-dot" style={{ background: entry.hex, display: 'inline-block', marginRight: 6 }} aria-hidden="true" />
                <span style={{ color: 'var(--df-ink)', fontWeight: 600 }}>{entry.title}</span>
              </button>
              <span className="df-dim" style={{ fontSize: 10 }}>{entry.kindLabel} · {entry.meta}</span>
            </div>
            {entry.body
              ? <p className="df-script-body">{entry.body}</p>
              : <p className="df-script-body df-dim">这个节点还没有内容。</p>}
          </article>
        ))}
    </div>
  )
}
