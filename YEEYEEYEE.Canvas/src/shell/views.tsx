import { NODE_KINDS, kindOf, recordStatus, recordTitle, type ChapterGroup, type ScriptEntry } from './records'

/**
 * 时间轴与剧本两个视图。
 *
 * 桌面端这两个视图是把当前画布的真实数据摊开（时间轴读章节/分镜/工作树项/成品，
 * 剧本把章节与分镜里的文字按顺序铺开），所以这里也一律读真实记录，不放占位数字。
 * 分组与排序规则在 records.ts 里（纯函数、有回归钉着），这两个组件只负责摆。
 */

export type TimelineViewProps = {
  groups: ChapterGroup[]
  activeChapter: string
  selectedId: string
  onChapter: (chapterId: string) => void
  onSelect: (recordId: string) => void
}

export function TimelineView({ groups, activeChapter, selectedId, onChapter, onSelect }: TimelineViewProps) {
  if (groups.length === 0) return <div className="df-empty">当前画布没有可显示的章节或节点。</div>
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
                  <button
                    key={record.recordId}
                    type="button"
                    className={`df-lane-chip${record.recordId === selectedId ? ' is-selected' : ''}`}
                    onClick={() => onSelect(record.recordId)}
                  >
                    <span className="df-node-kind">
                      <span className="df-node-dot" style={{ background: NODE_KINDS[kindOf(record.recordType)].hex }} aria-hidden="true" />
                      <span>{NODE_KINDS[kindOf(record.recordType)].label}</span>
                    </span>
                    <span className="df-node-title df-ellipsis" style={{ display: 'block', marginTop: 4 }}>{recordTitle(record)}</span>
                    <span className="df-dim df-mono" style={{ fontSize: 10 }}>{recordStatus(record)}</span>
                  </button>
                ))}
              </div>
            )}
        </section>
      ))}
    </div>
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
