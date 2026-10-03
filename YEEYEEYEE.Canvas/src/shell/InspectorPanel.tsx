import { recordReferences, resolveReference, type Asset } from '../assets'
import { chapterIdOf } from '../ChapterView'
import { describeLease, type Lease } from './locks'
import { chapterGroups, kindOf, NODE_KINDS, recordStatus, type ViewRecord } from './records'

/**
 * 节点检查器：选中节点后在这里改名与内容，写回画布。
 *
 * 布局与桌面端一致（节点名称 / 节点内容 / 应用修改 / 所属章节 / 节点状态五段）。
 * 桌面端那两张「所属章节 / 节点状态」原先摆的是写死的演示数据，后来换成了真实值，
 * 这里也一样：章节来自节点的稳定 chapterId，状态来自服务端的 readOnly 与当前角色权限。
 *
 * 引用那一节网页端比桌面端**多**：服务端已经把 references 投影过来了，不显示反而是浪费。
 */

export type InspectorProps = {
  selected: ViewRecord | null
  records: ViewRecord[]
  title: string
  content: string
  dirty: boolean
  saving: boolean
  readOnly: boolean
  canEdit: boolean
  hint: string
  /** 别人正锁着这个节点（含整棵树锁）：能看不能改。 */
  blockedBy?: Lease | null
  /** 自己正握着这个节点的锁：别人这会儿改不了。 */
  heldByMe?: boolean
  /** 编辑框是否拿到焦点。界面用它决定「该不该去占锁」。 */
  onEditingChange?: (focused: boolean) => void
  /** 删掉选中的这个节点（连带它的连线）。没给就不显示那个按钮。 */
  onDelete?: () => void
  assets: Asset[]
  assetsReady: boolean
  onTitle: (value: string) => void
  onContent: (value: string) => void
  onApply: () => void
}

export function InspectorPanel(props: InspectorProps) {
  const { selected, records, title, content, dirty, saving, readOnly, canEdit, hint, assets, assetsReady, onTitle, onContent, onApply } = props
  const blockedBy = props.blockedBy ?? null
  const heldByMe = props.heldByMe === true
  const editable = !!selected && canEdit && !readOnly && !blockedBy
  const chapterLabel = selected ? chapterGroups(records).find((group) => group.id === chapterIdOf(selected) && group.id.length > 0)?.label : undefined
  const kindLabel = selected ? NODE_KINDS[kindOf(selected.recordType)].label : ''
  // 顺序即优先级：被别人锁着最要紧（改不了），其次是自己握着。
  const status = blockedBy
    ? { text: `${describeLease(blockedBy)} 正在编辑`, color: 'var(--df-warning)', title: '别人正拿着这个节点的锁，等他保存或让管理员接管' }
    : heldByMe
      ? { text: '你正在编辑', color: 'var(--df-primary)', title: '锁在你手上，其他人现在改不了这个节点；离开编辑框后自动还回去' }
      : readOnly
        ? { text: '只读', color: 'var(--df-warning)', title: '服务端判定这张画布只能读（高版本格式、校验有错或迁移有歧义）' }
        : canEdit
          ? { text: '可编辑', color: 'var(--df-success)', title: '可以改节点标题与内容；点进编辑框时会先占锁' }
          : { text: '无编辑权限', color: 'var(--df-error)', title: '当前角色没有 canvas.edit 权限' }

  return (
    <div className="df-section" style={{ gap: 16 }}>
      {/* 结构级动作与「改标题内容」分开摆：它动的是画布结构，服务端按结构级对待（要整棵树锁）。 */}
      {props.onDelete && props.selected && (
        <div className="df-section" style={{ gap: 6 }}>
          <span className="df-label">画布结构</span>
          <button type="button" className="df-mini-button" onClick={props.onDelete}>
            删除这个节点（连同它的连线）
          </button>
        </div>
      )}
      <div className="df-section">
        {/* 焦点进到这两个框里 = 「打算改」，这时才去占锁（见 shouldHoldNodeLease）。
            监听挂在这一层而不是逐个输入框：在名称与内容之间用 Tab 或鼠标切换时焦点没离开这个区域，
            不该把锁还回去再申请一遍。relatedTarget 落在区域外才算真的离开。 */}
        <div
          className="df-section"
          onFocusCapture={() => props.onEditingChange?.(true)}
          onBlurCapture={(event) => {
            if (!event.currentTarget.contains(event.relatedTarget as Node | null)) props.onEditingChange?.(false)
          }}
        >
          <span className="df-label">节点名称</span>
          <div className="df-field is-boxed">
            <input
              className="df-input"
              value={title}
              disabled={!editable || saving}
              placeholder={selected ? '' : '未选择节点'}
              onChange={(event) => onTitle(event.target.value)}
              onKeyDown={(event) => { if (event.key === 'Enter') onApply() }}
            />
          </div>

          <span className="df-label" style={{ marginTop: 6 }}>节点内容</span>
          <div className="df-field is-boxed" style={{ minHeight: 76 }}>
            <textarea
              className="df-input"
              style={{ minHeight: 60 }}
              value={content}
              disabled={!editable || saving}
              placeholder="这个节点还没有内容；在画布上选中一个节点后，可以直接在这一格里写"
              onChange={(event) => onContent(event.target.value)}
              onKeyDown={(event) => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) onApply() }}
            />
          </div>
        </div>

        <div className="df-section" style={{ gap: 6, marginTop: 6 }}>
          <button
            type="button"
            className="df-mini-button"
            style={{ alignSelf: 'flex-start' }}
            disabled={!editable || !dirty || saving}
            onClick={onApply}
          >{saving ? '保存中…' : '应用修改'}</button>
          <span className="df-dim" style={{ fontSize: 10, lineHeight: 1.6 }}>{hint}</span>
        </div>
      </div>

      <hr className="df-divider" />

      <div className="df-grid-2">
        <div className="df-card">
          <span className="df-label">所属章节</span>
          <div className="df-heading df-ellipsis" style={{ fontSize: 12, marginTop: 5 }} title={chapterLabel ?? '未归档'}>
            {selected ? (chapterLabel ?? '未归档') : '—'}
          </div>
        </div>
        <div className="df-card">
          <span className="df-label">节点状态</span>
          <div className="df-heading" style={{ fontSize: 12, marginTop: 5, color: status.color }} title={status.title}>
            {status.text}
          </div>
        </div>
      </div>

      {selected && (
        <>
          <hr className="df-divider" />
          <div className="df-section">
            <span className="df-heading" style={{ fontSize: 12 }}>节点引用</span>
            <span className="df-dim df-mono" style={{ fontSize: 10 }}>{kindLabel} · {recordStatus(selected)} · {selected.recordId}</span>
            {recordReferences(selected).length === 0 && <span className="df-notice">这个节点没有引用。</span>}
            {recordReferences(selected).map((reference, index) => {
              const resolved = resolveReference(reference, assets, assetsReady)
              return (
                <div key={`${reference.entityId}-${index}`} className="df-card" style={resolved.error ? { borderColor: 'rgba(224, 112, 126, 0.45)' } : undefined}>
                  <div className="df-row-between">
                    <span className="df-heading" style={{ fontSize: 12 }}>{resolved.asset?.name || reference.name || '未知实体'}</span>
                    <span className="df-dim" style={{ fontSize: 10 }}>{resolved.mode}</span>
                  </div>
                  <div className="df-dim df-mono" style={{ fontSize: 10, marginTop: 4, wordBreak: 'break-all' }}>{reference.entityId}</div>
                  {reference.variantId && <div className="df-dim df-mono" style={{ fontSize: 10, wordBreak: 'break-all' }}>变体 {reference.variantId}</div>}
                  {resolved.variant && <div className="df-muted" style={{ fontSize: 11 }}>{resolved.variant.name || '未命名变体'}</div>}
                  {resolved.version && <div className="df-muted" style={{ fontSize: 11 }}>{resolved.version.label || (resolved.version.number != null ? `v${resolved.version.number}` : '未命名版本')}</div>}
                  {resolved.error && <div className="df-notice is-error">{resolved.error}</div>}
                </div>
              )
            })}
          </div>
        </>
      )}

      {!selected && <div className="df-notice">在画布上点一个节点，这里会显示它的内容，并可以直接修改。</div>}
    </div>
  )
}
