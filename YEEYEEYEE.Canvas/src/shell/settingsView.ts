/**
 * 网页端「设置」那一页读的是什么。
 *
 * 这一页**只有读**，而且读了也没有密钥：密钥在盘上是 DPAPI 密文，服务端跑在 Linux 上根本解不开，
 * 就算解得开也不该解（那属于桌面端那台机器）。所以接口只回答「端点是什么、模型是什么、密钥配没配」。
 *
 * 与其它解析器同一个态度：形状不对就抛——这是我们自己服务端的响应，字段对不上是开发期错误；
 * 而「页面上一格空白」不会报错，只会让人以为没配。
 */

export type SettingsProvider = {
  id: string
  displayName: string
  endpoint: string
  model: string
  enabled: boolean
  hasKey: boolean
}

export type WebSettings = {
  configured: boolean
  note: string
  selectedProfileId: string
  active: { endpoint: string; model: string; apiFormat: string; hasKey: boolean }
  providers: SettingsProvider[]
  media: {
    imageEndpoint: string
    imageModel: string
    imageHasKey: boolean
    comfyUiBaseUrl: string
    comfyUiCheckpoint: string
    videoEndpoint: string
    videoModel: string
  }
  collaboration: { serverUrl: string; account: string; autoSync: boolean }
  disabledSkills: string[]
}

function bad(what: string): never {
  throw new Error(`设置接口的响应里 ${what} 不对（GET /api/web/settings）`)
}

const asText = (value: unknown): string => (typeof value === 'string' ? value : '')
const asFlag = (value: unknown): boolean => value === true
const asObject = (value: unknown): Record<string, unknown> => {
  if (typeof value !== 'object' || value === null) bad('某个分组')
  return value as Record<string, unknown>
}

export function parseSettings(raw: unknown): WebSettings {
  const root = asObject(raw)
  if (typeof root.configured !== 'boolean') bad('configured')

  if (!root.configured)
    return {
      configured: false,
      note: asText(root.note) || '这台服务端机器上还没有模型配置文件。',
      selectedProfileId: '',
      active: { endpoint: '', model: '', apiFormat: '', hasKey: false },
      providers: [],
      media: { imageEndpoint: '', imageModel: '', imageHasKey: false, comfyUiBaseUrl: '', comfyUiCheckpoint: '', videoEndpoint: '', videoModel: '' },
      collaboration: { serverUrl: '', account: '', autoSync: false },
      disabledSkills: []
    }

  const active = asObject(root.active)
  const media = asObject(root.media)
  const collaboration = asObject(root.collaboration)
  if (!Array.isArray(root.providers)) bad('providers')

  return {
    configured: true,
    note: asText(root.note),
    selectedProfileId: asText(root.selectedProfileId),
    active: {
      endpoint: asText(active.endpoint),
      model: asText(active.model),
      apiFormat: asText(active.apiFormat),
      hasKey: asFlag(active.hasKey)
    },
    providers: root.providers.map((entry) => {
      const row = asObject(entry)
      return {
        id: asText(row.id),
        displayName: asText(row.displayName),
        endpoint: asText(row.endpoint),
        model: asText(row.model),
        enabled: asFlag(row.enabled),
        hasKey: asFlag(row.hasKey)
      }
    }),
    media: {
      imageEndpoint: asText(media.imageEndpoint),
      imageModel: asText(media.imageModel),
      imageHasKey: asFlag(media.imageHasKey),
      comfyUiBaseUrl: asText(media.comfyUiBaseUrl),
      comfyUiCheckpoint: asText(media.comfyUiCheckpoint),
      videoEndpoint: asText(media.videoEndpoint),
      videoModel: asText(media.videoModel)
    },
    collaboration: {
      serverUrl: asText(collaboration.serverUrl),
      account: asText(collaboration.account),
      autoSync: asFlag(collaboration.autoSync)
    },
    disabledSkills: Array.isArray(root.disabledSkills) ? root.disabledSkills.map(asText).filter((id) => id.length > 0) : []
  }
}

export type SettingsRow = { label: string; value: string; warn?: boolean }

/** ComfyUI 连通性探针（GET /api/web/settings/comfyui）的响应。 */
export type ComfyUiProbe = {
  reachable: boolean
  baseUrl: string
  version: string
  device: string
  detail: string
}

export function parseComfyUiProbe(raw: unknown): ComfyUiProbe {
  // 不走 asObject：它那句报错说的是「某个分组」，用在探针响应上等于指错地方。
  if (typeof raw !== 'object' || raw === null || typeof (raw as Record<string, unknown>).reachable !== 'boolean')
    throw new Error('设置接口的响应里 reachable 不对（GET /api/web/settings/comfyui）')
  const root = raw as Record<string, unknown>
  return {
    reachable: root.reachable as boolean,
    baseUrl: asText(root.baseUrl),
    version: asText(root.version),
    device: asText(root.device),
    detail: asText(root.detail)
  }
}

/**
 * 把探针结果说成一句人话。
 *
 * **不通时把服务端给的那句原话带上**：它是这个页面唯一能解释「为什么不通」的东西——
 * 「HTTP 404 后面跟着一页 HTML」和「连接被拒绝」要修的是两件不同的事，压成一句
 * 「连接失败」等于把线索扔了。
 */
export function describeComfyUiProbe(probe: ComfyUiProbe): string {
  if (!probe.reachable) return `不通：${probe.detail || '后端没有回应'}`
  return `通：${probe.baseUrl}（版本 ${probe.version || '未知'}，设备 ${probe.device || '未知'}）`
}

/** 一格没配的值写法统一：直接写「未配置」，而不是留一个空字符串让人猜。 */
const orUnset = (value: string): { value: string; warn?: boolean } =>
  value ? { value } : { value: '未配置', warn: true }

const keyRow = (hasKey: boolean): SettingsRow =>
  hasKey ? { label: '密钥', value: '已配置' } : { label: '密钥', value: '未配置', warn: true }

/** 把设置摊成「三组、每组几行」——面板只负责画，判定与兜底都在这里（可测）。 */
export function settingsGroups(settings: WebSettings): Array<{ title: string; rows: SettingsRow[] }> {
  if (!settings.configured)
    return [{ title: '模型接入', rows: [{ label: '配置文件', value: settings.note, warn: true }] }]

  const { active, media, collaboration, providers, disabledSkills } = settings
  return [
    {
      title: '模型接入',
      rows: [
        { label: '当前端点', ...orUnset(active.endpoint) },
        { label: '当前模型', ...orUnset(active.model) },
        keyRow(active.hasKey),
        { label: '备用接入点', value: providers.length === 0 ? '没有备用接入点' : `${providers.filter((p) => p.enabled).length} 个已启用 / 共 ${providers.length} 个` }
      ]
    },
    {
      title: '生图与生视频',
      rows: [
        { label: '生图端点', ...orUnset(media.imageEndpoint) },
        { label: '生图模型', ...orUnset(media.imageModel) },
        keyRow(media.imageHasKey),
        { label: 'ComfyUI 地址', ...orUnset(media.comfyUiBaseUrl) },
        { label: 'ComfyUI 模型', ...orUnset(media.comfyUiCheckpoint) },
        { label: '视频端点', ...orUnset(media.videoEndpoint) },
        { label: '视频模型', ...orUnset(media.videoModel) }
      ]
    },
    {
      title: '协作与技能',
      rows: [
        { label: '协作服务器', ...orUnset(collaboration.serverUrl) },
        { label: '协作账号', ...orUnset(collaboration.account) },
        { label: '改完自动同步', value: collaboration.autoSync ? '开' : '关' },
        { label: '停用的内置技能', value: disabledSkills.length === 0 ? '没有停用' : disabledSkills.join('、') }
      ]
    }
  ]
}
