import type { Asset } from '../assets'

/**
 * 技能参数与实体引用的**界面侧**口径。
 *
 * 权威在服务端：声明（/api/web/skills）由它给，校验（类型 / 范围 / 枚举 / 必填）也由它做。
 * 这里只做两件事：把声明渲染成能编辑的控件，以及把用户填的文本折回 JSON 值。
 * 所以这里**不尝试**比服务端更宽松——填错的照样交给服务端按稳定错误码拒掉。
 */

export type ChatParameterKind = 'string' | 'number' | 'boolean' | 'enum' | 'array' | 'object'

/** 一个技能参数的声明（与服务端 ChatParameterSpec 对齐）。 */
export type ChatParameterSpec = {
  name: string
  kind: ChatParameterKind
  required: boolean
  description: string
  options: string[]
  editable: boolean
  minimum: number | null
  maximum: number | null
}

export type ChatSkillDeclaration = { id: string; name: string; parameters: ChatParameterSpec[] }

export type ChatAttachmentType = { mime: string; kind: string; maxBytes: number; referenceable: boolean }

export type ChatSkillCatalog = {
  skills: ChatSkillDeclaration[]
  attachmentTypes: ChatAttachmentType[]
  attachmentAccept: string
}

const KINDS: ChatParameterKind[] = ['string', 'number', 'boolean', 'enum', 'array', 'object']

/** 读 `/api/web/skills`：声明读不懂的那一项跳过（少一条声明比整页取不到技能好），不猜类型。 */
export function parseSkillCatalog(raw: unknown): ChatSkillCatalog {
  const source = raw && typeof raw === 'object' ? raw as Record<string, unknown> : {}
  const skills = Array.isArray(source.skills) ? source.skills.flatMap((entry): ChatSkillDeclaration[] => {
    if (!entry || typeof entry !== 'object') return []
    const row = entry as Record<string, unknown>
    const id = typeof row.id === 'string' ? row.id : ''
    if (!id) return []
    return [{ id, name: typeof row.name === 'string' ? row.name : id, parameters: parseParameters(row.parameters) }]
  }) : []
  const attachmentTypes = Array.isArray(source.attachments) ? source.attachments.flatMap((entry): ChatAttachmentType[] => {
    if (!entry || typeof entry !== 'object') return []
    const row = entry as Record<string, unknown>
    if (typeof row.mime !== 'string' || !row.mime) return []
    return [{
      mime: row.mime,
      kind: typeof row.kind === 'string' ? row.kind : 'Other',
      maxBytes: typeof row.maxBytes === 'number' && Number.isFinite(row.maxBytes) ? row.maxBytes : 0,
      referenceable: row.referenceable === true
    }]
  }) : []
  return { skills, attachmentTypes, attachmentAccept: typeof source.attachmentAccept === 'string' ? source.attachmentAccept : '' }
}

function parseParameters(raw: unknown): ChatParameterSpec[] {
  if (!Array.isArray(raw)) return []
  return raw.flatMap((entry): ChatParameterSpec[] => {
    if (!entry || typeof entry !== 'object') return []
    const row = entry as Record<string, unknown>
    const name = typeof row.name === 'string' ? row.name : ''
    const kind = typeof row.kind === 'string' ? row.kind as ChatParameterKind : null
    if (!name || !kind || !KINDS.includes(kind)) return []
    return [{
      name, kind,
      required: row.required === true,
      description: typeof row.description === 'string' ? row.description : '',
      options: Array.isArray(row.options) ? row.options.filter((item): item is string => typeof item === 'string') : [],
      editable: row.editable === true,
      minimum: typeof row.minimum === 'number' ? row.minimum : null,
      maximum: typeof row.maximum === 'number' ? row.maximum : null
    }]
  })
}

/** 某个技能的声明；没有声明就当它只收提示词（与服务端对未知技能的处理一致）。 */
export function skillDeclaration(catalog: ChatSkillCatalog | null, skillId: string): ChatParameterSpec[] {
  return catalog?.skills.find(skill => skill.id === skillId)?.parameters ?? []
}

/** 聊天内允许用户编辑的参数（模型自主决定的那些不进编辑器）。 */
export function editableParameters(specs: ChatParameterSpec[]): ChatParameterSpec[] {
  return specs.filter(spec => spec.editable)
}

/** 编辑器里的原始文本值。 */
export type ChatParameterValues = Record<string, string>

/**
 * 编辑器的初值：模型已经给了的照原样填，没给的留空。
 *
 * **不给枚举填默认值**：图片比例这类选择必须由用户明确点一下——替用户默认一个 1:1
 * 就是把「他选的比例」变成「我们猜的比例」。
 */
export function initialParameterValues(specs: ChatParameterSpec[], supplied: Record<string, unknown> | undefined): ChatParameterValues {
  const values: ChatParameterValues = {}
  for (const spec of editableParameters(specs)) {
    const value = supplied?.[spec.name]
    values[spec.name] = value === undefined || value === null ? '' : parameterValueText(spec, value)
  }
  return values
}

/** 把一个已有的参数值折成编辑器文本。 */
function parameterValueText(spec: ChatParameterSpec, value: unknown): string {
  if (value === undefined || value === null) return ''
  if (spec.kind === 'object' || spec.kind === 'array') return JSON.stringify(value)
  if (spec.kind === 'boolean') return value === true ? 'true' : 'false'
  return String(value)
}

export type ParameterParseResult = { ok: true; value: unknown } | { ok: false; error: string }

/** 把编辑器文本折回 JSON 值；折不出来就返回可读原因（服务端仍会再校验一次）。 */
export function parseParameterInput(spec: ChatParameterSpec, text: string): ParameterParseResult {
  const trimmed = text.trim()
  if (!trimmed) return { ok: false, error: `请填写 ${spec.description || spec.name}` }
  switch (spec.kind) {
    case 'number': {
      if (!/^-?\d+$/.test(trimmed)) return { ok: false, error: `${spec.name} 必须是整数` }
      const value = Number(trimmed)
      if (spec.minimum !== null && value < spec.minimum || spec.maximum !== null && value > spec.maximum)
        return { ok: false, error: `${spec.name} 必须在 ${spec.minimum} 到 ${spec.maximum} 之间` }
      return { ok: true, value }
    }
    case 'boolean':
      if (trimmed !== 'true' && trimmed !== 'false') return { ok: false, error: `${spec.name} 必须是 true 或 false` }
      return { ok: true, value: trimmed === 'true' }
    case 'enum':
      if (!spec.options.includes(trimmed)) return { ok: false, error: `${spec.name} 只能是 ${spec.options.join('、')}` }
      return { ok: true, value: trimmed }
    case 'array':
    case 'object': {
      let parsed: unknown
      try { parsed = JSON.parse(trimmed) } catch { return { ok: false, error: `${spec.name} 必须是合法 JSON` } }
      if (spec.kind === 'array') {
        if (!Array.isArray(parsed) || parsed.some(item => typeof item !== 'string' || !item.trim()))
          return { ok: false, error: `${spec.name} 必须是字符串数组` }
        return { ok: true, value: parsed }
      }
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return { ok: false, error: `${spec.name} 必须是对象` }
      if (Object.values(parsed as Record<string, unknown>).some(value => !['string', 'number', 'boolean'].includes(typeof value)))
        return { ok: false, error: `${spec.name} 的字段只能是字符串、数字或布尔值` }
      return { ok: true, value: parsed }
    }
    default:
      return { ok: true, value: trimmed }
  }
}

/**
 * 把编辑器里的值收成提交用的参数对象。
 *
 * **只有用户真的改过或填过的参数才提交**：把空值也发上去会把「我没动它」变成
 * 「我要求它是空」，而服务端的必填与枚举校验只该在用户确实表达了意图时才拒绝他。
 */
export function buildParameterPayload(specs: ChatParameterSpec[], values: ChatParameterValues): { parameters: Record<string, unknown>; errors: string[] } {
  const parameters: Record<string, unknown> = {}
  const errors: string[] = []
  for (const spec of editableParameters(specs)) {
    const text = (values[spec.name] ?? '').trim()
    if (!text) {
      if (spec.required) errors.push(`缺少必填参数 ${spec.name}`)
      continue
    }
    const parsed = parseParameterInput(spec, text)
    if (!parsed.ok) { errors.push(parsed.error); continue }
    parameters[spec.name] = parsed.value
  }
  return { parameters, errors }
}

/**
 * 数组参数的候选值：`referenceAttachmentIds` 的候选就是**本条会话已上传的图片附件**。
 *
 * 为什么按名字认这一个：这个参数名是协议固定的（服务端只认图片、且必须来自本条会话
 * 更早的用户消息），它的候选来源无法从「一个字符串数组」这个类型里推出来——
 * 声明里写的仍是类型，界面上摆什么候选由协议语义决定。其余数组参数没有候选，
 * 编辑器回落到 JSON 文本输入。
 */
export function referenceAttachmentChoices(images: { id: string; name: string }[]): ArrayChoice[] {
  return images.map(image => ({ value: image.id, label: image.name || image.id }))
}

export type ArrayChoice = { value: string; label: string }

/** 数组参数当前选中的值（JSON 文本 → 字符串数组）；读不出来当空，不猜。 */
export function arrayParameterValue(text: string): string[] {
  const trimmed = text.trim()
  if (!trimmed) return []
  try {
    const parsed: unknown = JSON.parse(trimmed)
    return Array.isArray(parsed) ? parsed.filter((item): item is string => typeof item === 'string') : []
  } catch { return [] }
}

/** 切换数组参数里的一个取值，写回 JSON 文本（顺序按候选顺序，界面每次重算都一样）。 */
export function toggleArrayParameter(text: string, value: string): string {
  const current = arrayParameterValue(text)
  const next = current.includes(value) ? current.filter(item => item !== value) : [...current, value]
  return JSON.stringify(next)
}

/** 实体引用类型的中文；认不出的如实回原值，不猜。 */
export function entityReferenceKindLabel(kind: string): string {
  switch (kind) {
    case 'character': return '人物'
    case 'scene': return '场景'
    case 'prop': return '道具'
    case 'node': return '项目节点'
    default: return kind.trim() || '未知类型'
  }
}

/** 提交给服务端的实体引用（只填身份，名字与版本由服务端按 ID 回填）。 */
export type EntityReferenceInput = { kind: string; id: string; variantId?: string; versionId?: string }

export type EntityReferenceOption = { value: string; label: string; reference: EntityReferenceInput }

/** 下拉里的一行：可选的项目节点。 */
export type NodeChoice = { id: string; title: string }

/**
 * 把「当前画布的节点」与「项目资源库的实体」摊成可挑的引用条目。
 *
 * 身份是 ID；标签只用来显示。同一实体没有变体时也给一行（服务端会落到它的第一个变体）。
 */
export function entityReferenceOptions(nodes: NodeChoice[], assets: Asset[]): EntityReferenceOption[] {
  const options: EntityReferenceOption[] = nodes.filter(node => node.id).map(node => ({
    value: encodeEntityReference({ kind: 'node', id: node.id }),
    label: `项目节点 · ${node.title || node.id}`,
    reference: { kind: 'node', id: node.id }
  }))
  for (const asset of assets) {
    const kind = entityKindOfAsset(asset)
    if (!kind) continue
    const variants = asset.variants ?? []
    if (variants.length === 0) {
      options.push({
        value: encodeEntityReference({ kind, id: asset.id }),
        label: `${entityReferenceKindLabel(kind)} · ${asset.name}`,
        reference: { kind, id: asset.id }
      })
      continue
    }
    for (const variant of variants) {
      options.push({
        value: encodeEntityReference({ kind, id: asset.id, variantId: variant.id }),
        label: `${entityReferenceKindLabel(kind)} · ${asset.name}${variant.name ? ` · ${variant.name}` : ''}`,
        reference: { kind, id: asset.id, variantId: variant.id }
      })
    }
  }
  return options
}

/** 资源库里的 kind 是实体种类名（Character / Scene / Prop）；认不出的不给可选项，不猜。 */
function entityKindOfAsset(asset: Asset): string | null {
  switch ((asset.kind ?? '').toLowerCase()) {
    case 'character': return 'character'
    case 'scene': return 'scene'
    case 'prop': return 'prop'
    default: return null
  }
}

/** 选项值：`kind|id|variantId|versionId`；用 `|` 拼是因为四个字段都不含它。 */
export function encodeEntityReference(reference: EntityReferenceInput): string {
  return [reference.kind, reference.id, reference.variantId ?? '', reference.versionId ?? ''].join('|')
}

/** 拆回引用；缺类型或 ID 就返回 null——那不是这里生成的选项值。 */
export function decodeEntityReference(value: string): EntityReferenceInput | null {
  const [kind, id, variantId = '', versionId = ''] = value.split('|')
  if (!kind || !id) return null
  return { kind, id, variantId: variantId || undefined, versionId: versionId || undefined }
}

/** 聊天消息里的一条实体引用（服务端回填过名字与版本）。 */
export type ChatEntityReference = {
  kind: string
  id: string
  name?: string
  variantId?: string
  variantName?: string
  versionId?: string
  version?: string
  category?: string
}

/** 一条引用的一行摘要；名字缺失时回落到 ID（不编名字）。 */
export function entityReferenceLabel(reference: ChatEntityReference): string {
  const name = reference.name?.trim() || reference.id
  return [entityReferenceKindLabel(reference.kind) + '「' + name + '」', reference.variantName, reference.version]
    .filter((part): part is string => !!part && !!part.trim())
    .join(' · ')
}

/** 媒体种类 → 预览方式；读不出来的一律走链接降级，不在页面上硬渲染。 */
export function attachmentPreviewMode(kind: string): 'image' | 'video' | 'audio' | 'link' {
  switch (kind) {
    case 'Image': return 'image'
    case 'Video': return 'video'
    case 'Audio': return 'audio'
    default: return 'link'
  }
}

/** 附件的 accept 属性：优先用服务端声明的（加类型只改服务端一处）。 */
export function attachmentAccept(catalog: ChatSkillCatalog | null): string {
  return catalog?.attachmentAccept || 'image/png,image/jpeg,text/plain'
}

/** 某一种 MIME 的大小上限（字节）；服务端没声明就是 0（不编限制）。 */
export function attachmentMaxBytes(catalog: ChatSkillCatalog | null, mime: string): number {
  return catalog?.attachmentTypes.find(type => type.mime === mime)?.maxBytes ?? 0
}

/** 人类可读的上限文案。 */
export function formatBytes(bytes: number): string {
  if (bytes <= 0) return ''
  if (bytes >= 1024 * 1024) return `${Math.round(bytes / 1024 / 1024)}MiB`
  return `${Math.round(bytes / 1024)}KiB`
}
