import { useState } from 'react'
import { ALL_CHAPTERS_ID } from '../ChapterView'
import { CREATABLE_RECORD_TYPES, NODE_KINDS, kindOf, recordStatus, recordTitle, type ChapterGroup } from './records'

/**
 * 左栏「工作树资源」里的那棵树。
 *
 * 桌面端这里是一个 TreeView，行模型是「章节 → 节点」，标记与标签都来自真实数据。
 * 网页端能拿到的就是投影出来的记录（章节行 + 节点），所以这棵树的结构与它一致：
 * 第一层是章节，第二层是章节里的节点；点章节等于「只看这一章」，点节点等于在画布上选中它。
 *
 * 展开状态记在本地：桌面端把它双绑进行模型里是为了左栏重建后不丢，网页端的树不重建，
 * 所以本地状态就够了，不必把它抬到上一层。
 */

export type ChapterTreeProps = {
  groups: ChapterGroup[]
  activeChapter: string
  selectedId: string
  onChapter: (chapterId: string) => void
  onSelect: (recordId: string) => void
  /** 在指定章节里新建一个节点。没给就不显示那个入口。 */
  onCreate?: (chapterId: string, recordType: string) => void
}

export function ChapterTree({ groups, activeChapter, selectedId, onChapter, onSelect, onCreate }: ChapterTreeProps) {
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({})
  const [newKind, setNewKind] = useState(CREATABLE_RECORD_TYPES[0].recordType)
  const total = groups.reduce((sum, group) => sum + group.records.length, 0)

  if (groups.length === 0) return <div className="df-notice">这张画布还没有章节或节点。</div>

  return (
    <div className="df-section" style={{ gap: 2 }}>
      {/* 新建入口放在树的顶上：它建的就是「当前这一章」里的节点，
          没选章节（看全部）时建在未分章里——建在哪一章是这一屏唯一需要交代的事。 */}
      {onCreate && (
        <div className="df-section" style={{ gap: 4, marginBottom: 6 }}>
          <span className="df-label">新建节点</span>
          <div style={{ display: 'flex', gap: 4, alignItems: 'center' }}>
            <select
              className="df-input"
              style={{ flex: 1 }}
              value={newKind}
              onChange={(event) => setNewKind(event.target.value)}
            >
              {CREATABLE_RECORD_TYPES.map((item) => (
                <option key={item.recordType} value={item.recordType}>{item.label}</option>
              ))}
            </select>
            <button
              type="button"
              className="df-mini-button"
              onClick={() => onCreate(activeChapter, newKind)}
              title={activeChapter === ALL_CHAPTERS_ID ? '没选章节：会建在「未分章」里' : '建在当前这一章里'}
            >
              新建
            </button>
          </div>
        </div>
      )}
      <button
        type="button"
        className={`df-nav-button${activeChapter === ALL_CHAPTERS_ID ? ' is-active' : ''}`}
        style={{ padding: '5px 6px', fontSize: 11 }}
        onClick={() => onChapter(ALL_CHAPTERS_ID)}
        title="显示全部章节"
      >
        <span className="df-row-between">
          <span>全部章节</span>
          <span className="df-dim">{total}</span>
        </span>
      </button>

      {groups.map((group) => {
        const key = group.id || 'unfiled'
        const isOpen = !collapsed[key]
        const isActive = activeChapter === group.id
        return (
          <div key={key}>
            <div className="df-row" style={{ gap: 2 }}>
              <button
                type="button"
                className="df-text-button"
                style={{ width: 16, color: 'var(--df-ink-3)' }}
                onClick={() => setCollapsed((current) => ({ ...current, [key]: isOpen }))}
                aria-expanded={isOpen}
                title={isOpen ? '收起' : '展开'}
              >
                {isOpen ? '▾' : '▸'}
              </button>
              <button
                type="button"
                className={`df-nav-button${isActive ? ' is-active' : ''}`}
                style={{ padding: '5px 6px', fontSize: 11, flex: 1 }}
                onClick={() => onChapter(group.id)}
                title="只看这一章"
              >
                <span className="df-row-between">
                  <span className="df-row df-ellipsis" style={{ gap: 6 }}>
                    <span className="df-node-dot" style={{ background: NODE_KINDS.chapter.hex }} aria-hidden="true" />
                    <span className="df-ellipsis">{group.label}</span>
                  </span>
                  <span className="df-dim">{group.records.length}</span>
                </span>
              </button>
            </div>

            {isOpen && group.records.map((record) => {
              const kind = kindOf(record.recordType)
              return (
                <button
                  key={record.recordId}
                  type="button"
                  className={`df-nav-button${selectedId === record.recordId ? ' is-active' : ''}`}
                  style={{ padding: '4px 6px 4px 24px', fontSize: 11 }}
                  onClick={() => onSelect(record.recordId)}
                  title={`${recordStatus(record)} · ${record.recordId}`}
                >
                  <span className="df-row" style={{ gap: 6 }}>
                    <span className="df-node-dot" style={{ background: NODE_KINDS[kind].hex }} aria-hidden="true" />
                    <span className="df-ellipsis">{recordTitle(record)}</span>
                  </span>
                </button>
              )
            })}

            {isOpen && group.records.length === 0 && (
              <div className="df-dim" style={{ fontSize: 10, padding: '4px 6px 4px 24px' }}>这一章还没有节点。</div>
            )}
          </div>
        )
      })}
    </div>
  )
}
