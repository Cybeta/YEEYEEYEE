import { useEffect, useState } from 'react'
import { errorMessage, request, canvasUrl } from '../api'
import { mapAssets, type Asset } from '../assets'

export function EntityManager({ canEdit, onChanged }: { canEdit: boolean; onChanged: () => Promise<void> }) {
  const [entities, setEntities] = useState<Asset[]>([])
  const [revision, setRevision] = useState<number | null>(null)
  const [name, setName] = useState('')
  const [kind, setKind] = useState('Character')
  const [entityId, setEntityId] = useState('')
  const [variantId, setVariantId] = useState('')
  const [versionId, setVersionId] = useState('')
  const [core, setCore] = useState('')
  const [aliases, setAliases] = useState('')
  const [description, setDescription] = useState('')
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  async function load() {
    const result = await request('/api/web/assets') as { revision: number }
    setEntities(mapAssets(result).entities)
    setRevision(result.revision)
  }
  useEffect(() => { void load().catch(error => setNotice(errorMessage(error))) }, [])
  const entity = entities.find(item => item.id === entityId)
  const variant = entity?.variants?.find(item => item.id === variantId)
  useEffect(() => { setCore(entity?.core ?? ''); setAliases(entity?.aliases ?? '') }, [entity])
  useEffect(() => { setDescription(variant?.description ?? ''); setVersionId('') }, [variant])
  async function save(operation: string, extra: Record<string, unknown> = {}) {
    if (!canEdit || busy || revision === null) return
    if (operation.startsWith('delete') && !window.confirm('确认删除？存在引用时服务端会阻止删除，历史内容保留在资源库备份中。')) return
    setBusy(true)
    try {
      const basic = ['createEntity', 'renameEntity', 'createVariant', 'renameVariant'].includes(operation)
      await request(basic ? '/api/web/entities/manage' : '/api/web/entities/edit', { method: 'POST', body: JSON.stringify({ operation, baseRevision: revision, name, kind, entityId: entityId || null, variantId: variantId || null, versionId: versionId || null, core, aliases, description, note, ...extra }) })
      await load()
      await onChanged()
      setName('')
      setNotice('项目资源库已保存。')
    } catch (error) { setNotice(errorMessage(error)) }
    finally { setBusy(false) }
  }
  async function upload(file: File | undefined) {
    if (!file || !canEdit || busy) return
    if (file.size > 8 * 1024 * 1024) { setNotice('文件不能超过 8 MB。'); return }
    try {
      const data = await new Promise<string>((resolve, reject) => {
        const reader = new FileReader()
        reader.onload = () => resolve(String(reader.result).split(',')[1] ?? '')
        reader.onerror = () => reject(new Error('文件读取失败'))
        reader.readAsDataURL(file)
      })
      await save('addMedia', { name: file.name, mediaType: file.type, data })
    } catch (error) { setNotice(errorMessage(error)) }
  }
  return <div className="df-section">
    <span className="df-heading">项目实体与变体</span>
    <select className="df-input" value={entityId} disabled={busy} onChange={event => { setEntityId(event.target.value); setVariantId('') }}>
      <option value="">选择实体（新建时可留空）</option>
      {entities.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
    </select>
    <select className="df-input" value={variantId} disabled={busy} onChange={event => setVariantId(event.target.value)}>
      <option value="">选择变体</option>
      {entity?.variants?.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
    </select>
    {entity && <>
      <label className="df-field">实体核心描述<textarea className="df-input" value={core} disabled={busy || !canEdit} onChange={event => setCore(event.target.value)} /></label>
      <label className="df-field">别名<input className="df-input" value={aliases} disabled={busy || !canEdit} onChange={event => setAliases(event.target.value)} /></label>
      {canEdit && <button className="df-mini-button" disabled={busy} onClick={() => void save('editEntity')}>保存实体描述</button>}
    </>}
    {variant && <>
      <label className="df-field">变体描述<textarea className="df-input" value={description} disabled={busy || !canEdit} onChange={event => setDescription(event.target.value)} /></label>
      <span className="df-notice">当前 v{variant.currentVersion} · {variant.hasUncommittedChanges ? '有未提交改动' : '已提交'}</span>
      <select className="df-input" value={versionId} disabled={busy} onChange={event => setVersionId(event.target.value)}>
        <option value="">选择历史版本</option>
        {variant.versions?.map(v => <option key={v.id} value={v.id}>{v.label ?? `v${v.number}`}</option>)}
      </select>
      {canEdit && <>
        <input className="df-input" aria-label="版本备注" placeholder="版本备注" value={note} onChange={event => setNote(event.target.value)} />
        <div className="df-grid-2">
          <button className="df-mini-button" disabled={busy} onClick={() => void save('editVariant')}>保存变体描述</button>
          <button className="df-mini-button" disabled={busy || !variant.hasUncommittedChanges} onClick={() => void save('commitVersion')}>提交新版本</button>
          <button className="df-mini-button" disabled={busy || !versionId} onClick={() => void save('rollbackVersion')}>回滚到所选版本</button>
          <button className="df-mini-button" disabled={busy || !versionId} onClick={() => void save('deleteVersion')}>删除所选版本</button>
        </div>
        <label className="df-field">添加媒体（最多 8 MB）<input type="file" accept="image/png,image/jpeg,image/webp,video/mp4,audio/mpeg,audio/wav" disabled={busy} onChange={event => { void upload(event.target.files?.[0]); event.target.value = '' }} /></label>
      </>}
      {variant.attachments?.map(media => <div className="df-section" key={media.id}>
        <a href={canvasUrl(`/api/web/entities/${entityId}/variants/${variantId}/media/${media.id}`)} target="_blank" rel="noreferrer">{media.name || '未命名媒体'} · {media.kind}</a>
        {canEdit && <div className="df-grid-2">
          <button className="df-mini-button" disabled={busy || !name.trim()} onClick={() => void save('renameMedia', { attachmentId: media.id })}>用下方名称重命名</button>
          <button className="df-mini-button" disabled={busy} onClick={() => void save('removeMedia', { attachmentId: media.id })}>移除媒体附件</button>
        </div>}
      </div>)}
    </>}
    {canEdit && <>
      <input className="df-input" aria-label="实体或变体名称" placeholder="新名称" maxLength={200} value={name} onChange={event => setName(event.target.value)} />
      <select className="df-input" aria-label="实体类型" value={kind} onChange={event => setKind(event.target.value)}>
        <option value="Character">角色</option><option value="Scene">场景</option><option value="Prop">道具</option>
      </select>
      <div className="df-grid-2">
        <button className="df-mini-button" disabled={busy || !name.trim()} onClick={() => void save('createEntity')}>新建实体</button>
        <button className="df-mini-button" disabled={busy || !name.trim() || !entityId} onClick={() => void save('renameEntity')}>重命名实体</button>
        <button className="df-mini-button" disabled={busy || !name.trim() || !entityId} onClick={() => void save('createVariant')}>新建变体</button>
        <button className="df-mini-button" disabled={busy || !name.trim() || !variantId} onClick={() => void save('renameVariant')}>重命名变体</button>
        <button className="df-mini-button" disabled={busy || !entityId} onClick={() => void save('deleteEntity')}>删除实体</button>
        <button className="df-mini-button" disabled={busy || !variantId} onClick={() => void save('deleteVariant')}>删除变体</button>
      </div>
    </>}
    <button className="df-mini-button" disabled={busy} onClick={() => void load().then(() => setNotice('资源库已刷新。')).catch(error => setNotice(errorMessage(error)))}>刷新资源库</button>
    {notice && <span className="df-notice" role="status">{notice}</span>}
  </div>
}
