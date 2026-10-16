import { useEffect, useState } from 'react'
import { errorMessage, request } from '../api'
import { parseReferenceOption, recordAttachments, recordReferences, referenceOptions, type Asset, type AttachmentSummary } from '../assets'
import { attachmentFileUrl, withRetry } from './assetImages'
import { FilmPanel } from './FilmPanel'
import { chapterIdOf } from '../ChapterView'
import { describeLease, type Lease } from './locks'
import {
  attachmentKindLabel, failMedia, initialMediaLoad, isJobActive, jobFailureReason, jobStateHex, jobStateLabel,
  jobsForNode, mediaFailure, nodeMedia, resolveMediaPick, retryMedia, type MediaLoad, type MediaPick, type NodeJob
} from './nodeMedia'
import { referenceDetail, type ReferenceDetail, type ReferenceMedia } from './referenceDetail'
import {
  categoryOptions, chapterGroups, kindOf, NODE_KINDS, nodeConnections, nodeStatusOf, recordStatus, recordTitle,
  type ShellEdge, type ViewRecord
} from './records'

// 空数组放模块级：写成 `?? []` 每次渲染都是新数组，会让下面的 map 白算。
const EMPTY_EDGES: ShellEdge[] = []

/**
 * 节点检查器：选中节点后在这里改名与内容，写回画布。
 *
 * 布局与桌面端一致（节点名称 / 节点内容 / 应用修改 / 所属章节 / 节点状态五段）；
 * 最上面另有一块**画布结构**（连线的列表与断开、删除节点）——那些改的是画布结构而不是这个节点的文字，
 * 服务端按结构级对待（要整棵树锁），所以不混在「应用修改」里。
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
  /**
   * 画布上的连线。**断开连线放在这里**，而不是让线本身可点：
   * 线是 1.4px 的细条、命中区域太小，而且连线层是 `pointer-events: none` 的
   * （点线等于点空白，用来起平移）。检查器列的是「这个节点连着谁」，位置固定、点得准。
   */
  edges?: ShellEdge[]
  /** 断开一根连线。没给（只读角色、画布只读）就不显示那个按钮，但列表照旧显示。 */
  onDisconnect?: (edgeId: string) => void
  /**
   * 改这个节点的类别。没给就不显示那个下拉。
   * 注意它**不进草稿**——下拉一改就写一次（与旁边的名称/内容不同，那两个要点「应用修改」）。
   */
  onCategory?: (recordType: string) => void
  /**
   * 挂一条设定到这个节点上（**记录级**写入，与「改标题内容」同一档）。
   * 不给就不显示那个选择框——与「断开连线」「改类别」同一条规矩：没权限的入口不摆出来。
   */
  onAddReference?: (entityId: string, variantId: string | null) => void
  revision?: number
  onRegisterAttachment?: (attachmentId: string, entityId: string, variantId: string) => void
  /**
   * 画布上点了某条引用徽标：把对应引用的详情展开。
   * `token` 里带着它属于哪个节点（换节点时不能拿旧 token 去开新节点上的同名引用）
   * 与一个自增序号（同一条引用点第二次也能重新展开）。只影响展开状态，不改任何引用配置或画布数据。
   */
  focusReference?: { recordId: string; key: string; nonce: number }
  /**
   * 画布上点了某个节点自己出的图：把它设为主预览并打开大图。
   * 与 `focusReference` 同一条规矩：token 里带着节点 ID（换节点后旧 token 不能开新节点的图）
   * 与一个自增序号（同一张图点第二次也能重新打开）。只影响展示，不改任何画布数据。
   */
  focusMedia?: { recordId: string; attachmentId: string; nonce: number }
  /**
   * 当前的生成任务（`GET /api/web/jobs`）。任务属于它自己的目标节点（`targetNodeId`），
   * 所以这里按**选中节点的 ID** 挑出来，切到别的节点就换一批——不会把 A 的任务显示在 B 上。
   */
  jobs?: NodeJob[]
  /** 任务列表取不到时的原因。它只影响这一块，检查器其余部分照常可用。 */
  jobsError?: string
  assets: Asset[]
  assetsReady: boolean
  onTitle: (value: string) => void
  onContent: (value: string) => void
  onApply: () => void
}

export function InspectorPanel(props: InspectorProps) {
  const { selected, records, title, content, dirty, saving, readOnly, canEdit, hint, assets, assetsReady, onTitle, onContent, onApply } = props
  // 「这个节点出过哪些图」：服务端投影过来的**元数据**（里面没有路径），取字节按 ID 走接口。
  const attachments = selected ? recordAttachments(selected) : []
  // 这个节点身上的生成任务。**按节点的稳定 ID 挑**，与「任务是在哪儿发起的」无关：
  // 换成别的节点就换一批，A 的任务不会显示在 B 上。
  const nodeJobs = selected ? jobsForNode(props.jobs, selected.recordId) : []
  /**
   * 展开着详情的那条引用（键见 referenceDetail.key）。
   * 只在这里存**身份**，不存解析结果：节点/资源库一变就按新数据重算，不会留着上一份错配的内容。
   */
  const [openReference, setOpenReference] = useState('')
  // 换节点就把展开收起来：另一个节点上的同名引用不是同一条。
  useEffect(() => { setOpenReference('') }, [selected?.recordId])
  // 画布上点了引用徽标（token 里带着它属于哪个节点与一个序号）：展开对应的那条。
  // 声明在「换节点收起」之后，所以「点徽标」这一次重渲染里展开是最终状态。
  useEffect(() => {
    const focus = props.focusReference
    if (focus && focus.recordId === selected?.recordId) setOpenReference(focus.key)
  }, [props.focusReference])
  /**
   * 节点自身产物按种类摊开（顺序就是服务端给的：最近在前）。
   * 主预览只放图片与视频；音频与其它产物照旧在列表里直接可用，不因主预览改造丢掉入口。
   */
  const media = nodeMedia(attachments)
  const ownMedia = [...media.images, ...media.videos]
  /**
   * 现在主预览的是哪一个。**存的只是选择**，有效值在渲染时由 resolveMediaPick 现算：
   * 刷新、追加产物之后选中项还在就保留，被移除就回到最新的那一件；选择不跨节点沿用。
   */
  const [mediaPick, setMediaPick] = useState<MediaPick | null>(null)
  const picked = ownMedia.find(item => item.id === resolveMediaPick(ownMedia, selected?.recordId ?? '', mediaPick)) ?? null
  /** 大图浮层：只对图片开（视频在主预览里用原生播放器看）。 */
  const [viewing, setViewing] = useState<MediaPick | null>(null)
  // 换节点就把大图收起来：换回来时不该还停在上一个节点看过的那张图上。
  useEffect(() => { setViewing(null) }, [selected?.recordId])
  // 画布上点了节点自己的图：把它设成主预览并打开大图（与上面展开引用那一条同样的次序）。
  useEffect(() => {
    const focus = props.focusMedia
    if (!focus || focus.recordId !== selected?.recordId) return
    setMediaPick({ recordId: focus.recordId, attachmentId: focus.attachmentId })
    setViewing({ recordId: focus.recordId, attachmentId: focus.attachmentId })
  }, [props.focusMedia])
  // 大图里当前这一张在第几个：不在列表里（被删了、换了节点）就等于没开着。
  const viewingIndex = viewing && viewing.recordId === selected?.recordId
    ? media.images.findIndex(item => item.id === viewing.attachmentId)
    : -1
  const [targets, setTargets] = useState<{ entityId: string; variantId: string; name: string; variantName: string; version: number; bound: boolean }[]>([])
  const [targetError, setTargetError] = useState('')
  useEffect(() => {
    let active = true
    setTargets([]); setTargetError('')
    if (selected && props.onRegisterAttachment) {
      request<{ targets: typeof targets }>(`/api/web/records/${encodeURIComponent(selected.recordId)}/registration-targets`)
        .then(result => { if (active) setTargets(result.targets) })
        .catch(error => { if (active) setTargetError(errorMessage(error)) })
    }
    return () => { active = false }
  }, [selected?.recordId, props.revision, !!props.onRegisterAttachment])
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
  // 这个节点的连线。另一头的标题在这里解（纯函数只回答「谁连着谁」，不管那一头叫什么）。
  const connections = selected ? nodeConnections(props.edges ?? EMPTY_EDGES, selected.recordId) : []

  return (
    <div className="df-section" style={{ gap: 16 }}>
      {selected && kindOf(selected.recordType) === 'product' && <FilmPanel nodeId={selected.recordId} canEdit={editable} />}
      {/* 结构级动作与「改标题内容」分开摆：它动的是画布结构，服务端按结构级对待（要整棵树锁）。 */}
      {selected && (props.onDelete || connections.length > 0) && (
        <div className="df-section" style={{ gap: 6 }}>
          <span className="df-label">画布结构</span>
          {connections.map((connection) => {
            const other = records.find((record) => record.recordId === connection.otherId)
            const label = other ? recordTitle(other) : connection.otherId.slice(0, 8)
            return (
              <div key={connection.edgeId} className="df-card">
                <div className="df-row-between" style={{ gap: 6 }}>
                  <span className="df-muted df-ellipsis" style={{ fontSize: 11 }} title={`${connection.direction === 'out' ? '连到' : '来自'}：${label}（${connection.otherId}）`}>
                    {connection.direction === 'out' ? '→' : '←'} {label}
                  </span>
                  {props.onDisconnect && (
                    <button type="button" className="df-mini-button" onClick={() => props.onDisconnect!(connection.edgeId)}>断开</button>
                  )}
                </div>
              </div>
            )
          })}
          {props.onDelete && (
            <button type="button" className="df-mini-button" onClick={props.onDelete}>
              删除这个节点（连同它的连线）
            </button>
          )}
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

      {/* 节点类别：**不进草稿**，选一下就写一次。所以它单独一块并写明这一点——
          摆在编辑框旁边会让人以为要跟着「应用修改」一起提交。 */}
      {selected && props.onCategory && editable && (
        <div className="df-section" style={{ gap: 6 }}>
          <span className="df-label">节点类别</span>
          <div className="df-field is-boxed">
            <select
              className="df-input"
              value={selected.recordType}
              disabled={saving}
              onChange={(event) => props.onCategory!(event.target.value)}
            >
              {categoryOptions(selected.recordType).map((option) => (
                <option key={option.recordType} value={option.recordType}>{option.label}</option>
              ))}
            </select>
          </div>
          <span className="df-dim" style={{ fontSize: 10, lineHeight: 1.6 }}>
            选一下就写进画布（与名称 / 内容不同，它不进草稿）。改成章节要走桌面端。
          </span>
        </div>
      )}

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
            <span className="df-dim df-mono" style={{ fontSize: 10 }}>{kindLabel} · {nodeStatusOf(recordStatus(selected)).label} · {selected.recordId}</span>
            {recordReferences(selected).length === 0 && <span className="df-notice">这个节点没有引用。</span>}
            {recordReferences(selected).map((reference) => {
              const detail = referenceDetail(reference, assets, assetsReady)
              return (
                <ReferenceRow
                  key={detail.key}
                  detail={detail}
                  entityId={reference.entityId}
                  expanded={openReference === detail.key}
                  onToggle={() => setOpenReference((current) => current === detail.key ? '' : detail.key)}
                />
              )
            })}
            {/* 挂一条设定上来：与上面的「节点类别」同一条规矩——**选一下就写**，不进草稿。
                所以这里也单独一块并写明，不然会让人以为要跟着「应用修改」一起提交。
                选择框的值**永远钉在空串**上：选中即动作，不是状态；选完自己弹回占位项。 */}
            {selected && props.onAddReference && (
              <div className="df-section" style={{ gap: 6, marginTop: 4 }}>
                <span className="df-label">挂上设定</span>
                {!assetsReady
                  ? <span className="df-notice">资产库还没加载好，暂时挑不了设定。</span>
                  : (
                    <div className="df-field is-boxed">
                      <select
                        className="df-input"
                        value=""
                        disabled={saving}
                        onChange={(event) => {
                          const chosen = parseReferenceOption(event.target.value)
                          if (chosen) props.onAddReference!(chosen.entityId, chosen.variantId)
                        }}
                      >
                        <option value="">＋ 选一条设定挂上去（选一下就写）</option>
                        {referenceOptions(assets).map((option) => (
                          <option key={option.value} value={option.value}>{option.label}</option>
                        ))}
                      </select>
                    </div>
                  )}
              </div>
            )}
          </div>

          {/* 这个节点身上的生成任务。**逐条列**，不做汇总成一个状态：
              同一个节点上可能有两次生成，一个有进度、一个已经失败——合成一条就会丢掉其中一个。 */}
          {selected && (nodeJobs.length > 0 || props.jobsError) && (
            <div className="df-section">
              <span className="df-heading" style={{ fontSize: 12 }}>生成任务</span>
              <span className="df-dim" style={{ fontSize: 10 }}>任务属于发起时固定的目标节点；产物按任务分别回挂，互不覆盖。</span>
              {props.jobsError && <span role="alert" className="df-notice is-error">任务状态读取失败：{props.jobsError}</span>}
              {nodeJobs.map((job) => {
                const reason = jobFailureReason(job)
                return (
                  <div key={job.jobId} className="df-card">
                    <div className="df-row-between">
                      <span className="df-heading" style={{ fontSize: 12, color: jobStateHex(job.state) }}>{jobStateLabel(job.state)}</span>
                      <span className="df-dim df-mono" style={{ fontSize: 10 }}>{job.jobId.slice(0, 8)}</span>
                    </div>
                    {isJobActive(job.state) && (
                      <span className="df-dim" style={{ fontSize: 10 }}>进度 {Math.min(100, Math.max(0, job.progressPercent ?? 0))}%</span>
                    )}
                    {/* 「回挂中」只在任务**已经跑完**、产物正在写进节点时才是真的：
                        运行中的任务写它，会让人以为图马上就有了。 */}
                    {!isJobActive(job.state) && job.attachmentState === 'pending' && <span className="df-dim" style={{ fontSize: 10 }}>产物正在回挂到这个节点…</span>}
                    {reason && <span role="alert" className="df-notice is-error">{reason}</span>}
                  </div>
                )
              })}
            </div>
          )}

          {/* 这个节点出过哪些图。服务端只投影元数据（**没有路径**），图片按 ID 走接口取字节。
              上面是图片 / 视频的主预览（挑最近的那一件，可在下面切换），下面是逐条清单；
              音频与其它产物照旧在清单里直接可用——主预览只解释「图片/视频现在看的是哪一件」。 */}
          {selected && attachments.length > 0 && (
            <div className="df-section">
              <span className="df-heading" style={{ fontSize: 12 }}>产物附件</span>
              <span className="df-dim df-mono" style={{ fontSize: 10 }}>{attachments.length} 项 · 最近在前</span>
              {props.onRegisterAttachment && <>
                <span className="df-dim" style={{ fontSize: 10 }}>登记现有图片并提交新版本，保留历史；不会重新生成或收费。请先绑定明确的当前变体。</span>
                {targetError && <span className="df-notice is-error">{targetError}</span>}
                {targets.some(t => !t.bound) && props.onAddReference && <select className="df-input" value="" disabled={saving || dirty || !editable} onChange={event => {
                  const chosen = parseReferenceOption(event.target.value)
                  if (chosen) props.onAddReference!(chosen.entityId, chosen.variantId)
                }}>
                  <option value="">＋ 绑定画布内嵌实体变体</option>
                  {targets.filter(t => !t.bound).map(t => <option key={`${t.entityId}|${t.variantId}`} value={`${t.entityId}|${t.variantId}`}>{t.name} · {t.variantName} · {t.variantId.slice(0, 8)}</option>)}
                </select>}
              </>}
              {picked && (
                <div className="df-section" style={{ gap: 6 }}>
                  <div className="df-row-between" style={{ gap: 6 }}>
                    <span className="df-label">主预览</span>
                    <span className="df-dim" style={{ fontSize: 10 }}>{attachmentKindLabel(picked.kind)} · {picked.name}</span>
                  </div>
                  {/* key 带上节点与产物：换附件 / 换节点就是**新的一份加载状态**，
                      旧的失败、旧的播放器都不会跟过来（视频也就此停下）。 */}
                  <NodeMediaStage
                    key={`${selected.recordId}:${picked.id}`}
                    recordId={selected.recordId}
                    attachment={picked}
                    onOpen={(attachmentId) => setViewing({ recordId: selected.recordId, attachmentId })}
                  />
                </div>
              )}
              <div className="df-attachments">
                {attachments.map((attachment) => {
                  // 图片与视频的那一格就是**选择入口**：点它换主预览；音频照旧是行内播放器。
                  const selectable = attachment.kind === 'Image' || attachment.kind === 'Video'
                  const active = selectable && attachment.id === picked?.id
                  return (
                    // key 带上节点：两件不同节点的产物**有可能撞 ID**（Web 生成物的 ID 是从内容算出来的），
                    // 只按产物 ID 做键会让上一个节点的失败状态被这一个节点复用。
                    <div key={`${selected.recordId}:${attachment.id}`} className={`df-attachment${active ? ' is-active' : ''}`}>
                      {selectable
                        ? <button
                          type="button"
                          className="df-attachment-tile"
                          aria-pressed={active}
                          aria-label={`${active ? '当前主预览' : '设为主预览'}：${attachment.name}`}
                          title={`${attachmentKindLabel(attachment.kind)}${active ? ' · 当前主预览' : ' · 设为主预览'}`}
                          onClick={() => setMediaPick({ recordId: selected.recordId, attachmentId: attachment.id })}
                        ><AttachmentTileFace recordId={selected.recordId} attachment={attachment} /></button>
                        : <AttachmentMedia recordId={selected.recordId} attachment={attachment} />}
                      <div className="df-attachment-meta">
                        <a className="df-attachment-name" href={attachmentFileUrl(selected.recordId, attachment.id)} download={attachment.name}>下载 {attachment.name}</a>
                        <span className="df-dim" style={{ fontSize: 10 }}>{attachment.source || '来源未记'}</span>
                        {active && <span className="df-dim" style={{ fontSize: 10 }}>当前主预览{attachment.kind === 'Image' ? ' · 点主预览看大图' : ''}</span>}
                        {attachment.kind === 'Image' && props.onRegisterAttachment && <select aria-label={`登记 ${attachment.name} 到实体变体`} className="df-input" value="" disabled={saving || dirty || !editable || !targets.some(t => t.bound)} onChange={event => {
                          const target = targets.find(t => `${t.entityId}|${t.variantId}` === event.target.value && t.bound)
                          if (target && window.confirm(`将此图片登记到「${target.name} · ${target.variantName}」并提交新版本？现有附件与历史版本会保留。`)) props.onRegisterAttachment!(attachment.id, target.entityId, target.variantId)
                        }}>
                          <option value="">登记到变体并提交版本…</option>
                          {targets.filter(t => t.bound).map(t => <option key={`${t.entityId}|${t.variantId}`} value={`${t.entityId}|${t.variantId}`}>{t.name} · {t.variantName} · v{t.version} · {t.variantId.slice(0, 8)}</option>)}
                        </select>}
                      </div>
                    </div>
                  )
                })}
              </div>
            </div>
          )}

          {/* 节点自身图片的大图：铺满视口、点背景 / 关闭按钮 / Esc 收起，多图可左右切换。 */}
          {selected && viewingIndex >= 0 && (
            <NodeImageViewer
              recordId={selected.recordId}
              images={media.images}
              index={viewingIndex}
              onIndex={(next) => setViewing({ recordId: selected.recordId, attachmentId: media.images[next].id })}
              onClose={() => setViewing(null)}
            />
          )}
        </>
      )}

      {!selected && <div className="df-notice">在画布上点一个节点，这里会显示它的内容，并可以直接修改。</div>}
    </div>
  )
}

/**
 * 一条节点引用：标题一行 +（展开后）实体/变体/版本资料与**这一版实际解析到的媒体**。
 *
 * 展开只影响显示：不取锁、不写引用配置、不写画布（与桌面端「浮层只画不写」同一条规矩）。
 * 媒体状态按 `media.id` 分开存：换一条媒体看到的是它自己的状态，不会被上一条的失败挡住；
 * 这一行换了引用（`key` 变）时 React 会重建它，失败状态自然清空。
 */
function ReferenceRow({ detail, entityId, expanded, onToggle }: {
  detail: ReferenceDetail
  entityId: string
  expanded: boolean
  onToggle: () => void
}) {
  const [pickedId, setPickedId] = useState('')
  const [failures, setFailures] = useState<Record<string, string>>({})
  const [retry, setRetry] = useState(0)
  const [viewer, setViewer] = useState<{ src: string; label: string } | null>(null)
  // 选中的那条从**当前**媒体列表里现算：列表变了（版本切换、媒体被删）就不会留着一个已不存在的预览。
  const picked: ReferenceMedia | null = detail.media.find((item) => item.id === pickedId) ?? detail.media[0] ?? null
  const failure = picked ? failures[picked.id] ?? '' : ''
  const source = (url: string) => retry > 0 ? `${url}${url.includes('?') ? '&' : '?'}retry=${retry}` : url
  useEffect(() => {
    if (!viewer) return
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') setViewer(null) }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [viewer])
  const fail = (id: string, message: string) => setFailures((current) => ({ ...current, [id]: message }))
  const retryPicked = () => {
    if (!picked) return
    setFailures((current) => { const next = { ...current }; delete next[picked.id]; return next })
    setRetry((current) => current + 1)
  }
  const meta = [detail.entityKind,
    detail.variantName ? `变体 ${detail.variantName}` : '',
    `版本 ${detail.versionLabel}`, detail.mode].filter(Boolean).join(' · ')
  const mediaView = !picked
    ? <span className="df-dim" style={{ fontSize: 10 }}>{detail.note || '这一版没有可看的媒体。'}</span>
    : failure
      ? <span className="df-row" style={{ gap: 6 }}>
        <span role="alert" className="df-notice is-error">读取失败：{failure}</span>
        <button type="button" className="df-mini-button" onClick={retryPicked}>重试</button>
      </span>
      : !picked.url
        ? <span className="df-notice is-error">这条媒体没有可读取的地址（缺少变体 ID）。</span>
        : picked.kind === 'Image'
          ? <span className="df-section" style={{ gap: 4 }}>
            <img
              className="df-ref-media-image"
              src={source(picked.url)}
              alt={picked.name || '引用图片'}
              onError={() => fail(picked.id, '图片无法读取（文件不在这个部署里，或这个地址已失效）')}
            />
            <button type="button" className="df-mini-button" style={{ alignSelf: 'flex-start' }}
              onClick={() => setViewer({ src: source(picked.url!), label: `${detail.entityName} · ${picked.name || picked.kind}` })}>打开大图</button>
          </span>
          : picked.kind === 'Video'
            ? <video className="df-attachment-media" controls preload="metadata" src={source(picked.url)}
              onError={() => fail(picked.id, '视频无法读取（文件不在这个部署里，或编码不被浏览器支持）')} />
            : picked.kind === 'Audio'
              ? <audio className="df-attachment-media" controls preload="metadata" src={source(picked.url)}
                onError={() => fail(picked.id, '音频无法读取（文件不在这个部署里，或编码不被浏览器支持）')} />
              : <span className="df-dim" style={{ fontSize: 10 }}>这种媒体（{picked.kind}）网页端不能预览。</span>
  return (
    <div className="df-card" style={detail.state === 'broken' ? { borderColor: 'rgba(224, 112, 126, 0.45)' } : undefined}>
      <div className="df-row-between">
        <span className="df-heading" style={{ fontSize: 12 }}>{detail.entityName}</span>
        <span className="df-row" style={{ gap: 6 }}>
          <span className="df-dim" style={{ fontSize: 10 }}>{detail.mode}</span>
          <button type="button" className="df-mini-button" aria-expanded={expanded} onClick={onToggle}>{expanded ? '收起详情' : '详情'}</button>
        </span>
      </div>
      <div className="df-dim df-mono" style={{ fontSize: 10, marginTop: 4, wordBreak: 'break-all' }}>{entityId}</div>
      {detail.state === 'broken' && <div className="df-notice is-error">{detail.error}</div>}
      {expanded && (
        <div className="df-section df-ref-detail">
          <span className="df-dim df-mono" style={{ fontSize: 10, wordBreak: 'break-all' }}>{meta}</span>
          {/* 没有内容的字段不摆出来：空占位会让人以为「这里有内容但没显示」。 */}
          {detail.description && <span className="df-muted" style={{ fontSize: 11 }}>{detail.description}</span>}
          {detail.core && <span className="df-muted" style={{ fontSize: 11 }}>{detail.core}</span>}
          {detail.aliases && <span className="df-dim" style={{ fontSize: 10 }}>别名：{detail.aliases}</span>}
          {detail.media.length > 1 && (
            <span className="df-row df-wrap" style={{ gap: 4 }}>
              {detail.media.map((item) => (
                <button
                  key={item.id}
                  type="button"
                  className={`df-mini-button${item.id === picked?.id ? ' is-active' : ''}`}
                  aria-pressed={item.id === picked?.id}
                  onClick={() => setPickedId(item.id)}
                >{item.name || item.kind}</button>
              ))}
            </span>
          )}
          {picked && (
            <span className="df-dim" style={{ fontSize: 10 }}>
              {picked.name || '未命名媒体'} · {picked.kind} · {detail.mediaSource === 'version' ? `来自 ${detail.versionLabel}` : '来自当前变体'}
            </span>
          )}
          {mediaView}
          <span className="df-dim" style={{ fontSize: 10 }}>网页端只读引用详情；要改引用请到桌面端进引用画布。</span>
        </div>
      )}
      {viewer && (
        // 大图铺满视口：点背景、点关闭或按 Esc 收起；图上的点击不再往下传（不触到画布手势）。
        <div className="df-ref-viewer" role="dialog" aria-label="引用图片大图" onClick={() => setViewer(null)}>
          <img src={viewer.src} alt={viewer.label} onClick={(event) => event.stopPropagation()} />
          <div className="df-ref-viewer-bar">
            <span className="df-mono">{viewer.label}</span>
            <button type="button" className="df-mini-button" onClick={() => setViewer(null)}>关闭（Esc）</button>
          </div>
        </div>
      )}
    </div>
  )
}

/**
 * 一份受保护产物地址的加载状态：第几次尝试、当前这一次失败了没有、以及重试。
 *
 * 三件事一起才够用：
 *   1. 重试要换地址（`withRetry`）——同一个 URL 浏览器会拿旧结果，按下去等于没按；
 *   2. 失败只记在**它自己那一次**的序号上（`failMedia`），上一件产物 / 上一次尝试的迟到事件丢不进当前状态；
 *   3. 换附件 / 换节点由调用方换 `key` 重建实例，旧元素连同它的播放与失败一起消失。
 */
function useMediaLoad() {
  const [load, setLoad] = useState<MediaLoad>(initialMediaLoad)
  const attempt = load.attempt
  return {
    attempt,
    failure: mediaFailure(load),
    retry: () => setLoad(retryMedia),
    // 记失败时带上**这次渲染的序号**：旧元素的迟到事件带的是旧序号，会被 failMedia 原样丢掉。
    fail: (reason: string) => setLoad(current => failMedia(current, attempt, reason)),
    url: (base: string) => withRetry(base, attempt)
  }
}

/**
 * 节点自身图片 / 视频的**主预览**：图片点一下开大图，视频用原生播放器（controls + metadata 预加载，不自动播放）。
 *
 * 读不到时只换掉媒体本身：说明 + 重试按钮留在原地，附件名称与下面的选择入口照旧可用。
 * `key={attempt}` 让每次重试都是一枚**新的媒体元素**——旧元素被摘掉，它的迟到事件也就无处可去。
 */
function NodeMediaStage({ recordId, attachment, onOpen }: {
  recordId: string
  attachment: AttachmentSummary
  onOpen: (attachmentId: string) => void
}) {
  const media = useMediaLoad()
  const url = media.url(attachmentFileUrl(recordId, attachment.id))
  if (media.failure) return <div className="df-media-stage is-failed" role="alert">
    <span>{attachmentKindLabel(attachment.kind)}读取失败：{media.failure}</span>
    <button type="button" className="df-mini-button" onClick={media.retry}>重试</button>
  </div>
  if (attachment.kind === 'Image') return <button
    type="button"
    className="df-media-stage is-openable"
    title={`点击查看大图 · ${attachment.name}`}
    onClick={() => onOpen(attachment.id)}
  >
    <img
      key={media.attempt}
      className="df-media-stage-image"
      src={url}
      alt={attachment.name}
      onError={() => media.fail('图片无法读取（文件不在这个部署里，或这个地址已失效）')}
    />
  </button>
  return <div className="df-media-stage">
    <video
      key={media.attempt}
      className="df-media-stage-video"
      controls
      preload="metadata"
      src={url}
      onError={() => media.fail('视频无法读取（文件不在这个部署里，或编码不被浏览器支持）')}
    />
  </div>
}

/**
 * 列表里那一格 44×44 的缩略图：它是**选择入口**（点它换主预览），不是播放器。
 * 视频只放一枚「▶」——一个列表里塞进多个播放器会让同一件产物在两个位置同时播/同时抓元数据。
 * 图取不到就留一个同样大小的「无图」占位（不是把 img 收起来，也不是破图图标）；
 * 重试在主预览那一块（那里空间够，也不会嵌进卡片的按钮里）。
 */
function AttachmentTileFace({ recordId, attachment }: { recordId: string; attachment: AttachmentSummary }) {
  const [failed, setFailed] = useState(false)
  if (attachment.kind === 'Video') return <span className="df-attachment-kind" aria-hidden="true">▶</span>
  if (failed) return <span className="df-attachment-kind" role="alert" title="图片无法读取：点这一格设为主预览后可重试">无图</span>
  return <img
    className="df-attachment-thumb"
    src={attachmentFileUrl(recordId, attachment.id)}
    alt=""
    loading="lazy"
    onError={() => setFailed(true)}
  />
}

/**
 * 音频与其它产物：**保留原有访问能力**（音频仍是同一份字节上的行内播放器），
 * 只是不再进主预览——主预览讲的是「图片 / 视频现在看的是哪一件」。
 * 读不到时给一句明确的话 + 重试，而不是一个按下去没反应的播放器。
 */
function AttachmentMedia({ recordId, attachment }: { recordId: string; attachment: AttachmentSummary }) {
  const media = useMediaLoad()
  if (attachment.kind === 'Audio') {
    if (media.failure) return <span className="df-attachment-failed" role="alert">
      <span className="df-dim" style={{ fontSize: 10 }}>{media.failure}</span>
      <button type="button" className="df-mini-button" onClick={media.retry}>重试</button>
    </span>
    return <audio
      key={media.attempt}
      className="df-attachment-media"
      controls
      preload="metadata"
      src={media.url(attachmentFileUrl(recordId, attachment.id))}
      onError={() => media.fail('音频无法读取（文件不在这个部署里，或编码不被浏览器支持）')}
    />
  }
  return <span className="df-attachment-kind" title={attachmentKindLabel(attachment.kind)}>{attachmentKindLabel(attachment.kind)}</span>
}

/**
 * 节点自身图片的大图：复用引用大图那一套浮层样式（铺满视口、点背景或 Esc 收起），
 * 多图时在底部给一条缩略带（当前那一张标出来），另给上一张 / 下一张。
 *
 * 浮层挂在这里而不是画布里：画布世界整层带 transform，`position: fixed` 在里面会变成
 * 相对那层定位；挂在这一层才是真的铺满视口，也天然不会把点击落回画布手势上。
 * 图片按 `max-width/max-height` 收在视口里，宽图、长图都不会撑破页面。
 *
 * 导出是为了能在测试里直接钉住它的结构（切换、当前项、关闭与重试都长在这一层）：
 * 它由 `viewing` 状态决定开不开，整块检查器渲染时默认是关着的，看不见内部。
 */
export function NodeImageViewer({ recordId, images, index, onIndex, onClose }: {
  recordId: string
  images: AttachmentSummary[]
  index: number
  onIndex: (index: number) => void
  onClose: () => void
}) {
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])
  const current = images[index]
  const step = (delta: number) => onIndex((index + delta + images.length) % images.length)
  return (
    // is-multi 让大图给底下的缩略带留出高度：不带它时浮层是按「一张图 + 一行说明」算的，
    // 多图时缩略带会被压扁（它是 flex 项，默认会被挤）。
    <div className={`df-ref-viewer${images.length > 1 ? ' is-multi' : ''}`} role="dialog" aria-label="节点产物大图" onClick={onClose}>
      {/* key 用当前这一张的 ID：换图就是**新的一份加载状态**，上一张的失败与迟到事件都留不下。 */}
      <ViewerImage key={current.id} recordId={recordId} image={current} />
      <div className="df-ref-viewer-bar" onClick={(event) => event.stopPropagation()}>
        <button type="button" className="df-mini-button" disabled={images.length < 2} onClick={() => step(-1)}>上一张</button>
        <span className="df-mono df-ellipsis" title={current.name}>{current.name}</span>
        <span className="df-dim">第 {index + 1} / {images.length} 张</span>
        <button type="button" className="df-mini-button" disabled={images.length < 2} onClick={() => step(1)}>下一张</button>
        <button type="button" className="df-mini-button" onClick={onClose}>关闭（Esc）</button>
      </div>
      {images.length > 1 && <div className="df-media-viewer-strip" onClick={(event) => event.stopPropagation()}>
        {images.map((image, at) => (
          <button
            key={image.id}
            type="button"
            className={`df-media-viewer-thumb${at === index ? ' is-active' : ''}`}
            aria-current={at === index}
            aria-label={`第 ${at + 1} 张：${image.name}`}
            title={image.name}
            onClick={() => onIndex(at)}
          >
            <img className="df-attachment-thumb" src={attachmentFileUrl(recordId, image.id)} alt="" loading="lazy" />
          </button>
        ))}
      </div>}
    </div>
  )
}

/** 大图里当前这一张：读不到时换成说明 + 重试（重试同样换一次地址序号，真的重新取）。 */
function ViewerImage({ recordId, image }: { recordId: string; image: AttachmentSummary }) {
  const media = useMediaLoad()
  if (media.failure) return <div className="df-media-viewer-failure" role="alert">
    <span>{image.name}：{media.failure}</span>
    <button type="button" className="df-mini-button" onClick={(event) => { event.stopPropagation(); media.retry() }}>重试</button>
  </div>
  return <img
    key={media.attempt}
    src={media.url(attachmentFileUrl(recordId, image.id))}
    alt={image.name}
    onClick={(event) => event.stopPropagation()}
    onError={() => media.fail('图片无法读取（文件不在这个部署里，或这个地址已失效）')}
  />
}
