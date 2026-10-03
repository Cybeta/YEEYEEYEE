import { uiText } from './uiText'

/**
 * 网页端节点右键菜单的内容。
 *
 * 「有哪些选项」这件事**不是网页端定的**：它由服务端从共享的 `NodeAssistPlanner` 算出来
 * （`GET /api/web/records/{id}/assist`）——桌面端右键菜单用的就是同一份计划。所以这里的活是：
 * 把计划里那条建议，翻成「这一端现在能不能跑、点下去做什么」。
 *
 * **翻译的规则只有一条**：计划说能不能跑（`blocked`），网页端说**自己有没有那个执行方**。
 * 两件事都成立才是可点的；任何一条不成立就灰着，并把原因写在菜单项上——
 * 灰着但不说话，用户只会以为菜单坏了（这是这个仓库里反复出现的那个取舍）。
 */

/** 服务端给的种类（`NodeAssistKind` 的名字）。 */
export type NodeAssistKind = 'Prompt' | 'Agent' | 'Image' | 'Video'

export type NodeAssistSuggestion = {
  id: string
  title: string
  kind: NodeAssistKind
  /** 桌面端用它在自己的技能表里找人；网页端的技能表里没有这些 ID，所以只作备案。 */
  skillId: string
  prompt: string
  negativePrompt: string
  /** 非空 = 现在跑不了，内容是原因（服务端算的，「这个节点还没写内容」这类）。 */
  blocked: string
  canRun: boolean
}

export type NodeAssistPlan = {
  recordId: string
  summary: string
  hasUpstream: boolean
  suggestions: NodeAssistSuggestion[]
}

const KINDS: NodeAssistKind[] = ['Prompt', 'Agent', 'Image', 'Video']

function bad(what: string): never {
  throw new Error(`节点协助计划的响应里 ${what} 不对（GET /api/web/records/{id}/assist）`)
}

function text(value: unknown, what: string): string {
  if (typeof value !== 'string') bad(what)
  return value
}

/**
 * 校验并解析 `GET /api/web/records/{id}/assist` 的响应。
 *
 * 形状不对就抛：这是**我们自己服务端**的响应，字段对不上是开发期错误，当场炸比
 * 「菜单上少几项、没人发现」好。菜单少一项不会报错，只会让人觉得这个功能很弱。
 */
export function parseNodeAssist(raw: unknown): NodeAssistPlan {
  if (typeof raw !== 'object' || raw === null) bad('整体')
  const source = raw as Record<string, unknown>
  const list = source.suggestions
  if (!Array.isArray(list)) bad('suggestions')

  const suggestions = list.map((entry) => {
    if (typeof entry !== 'object' || entry === null) bad('某条建议')
    const item = entry as Record<string, unknown>
    const kind = text(item.kind, '某条建议的 kind')
    if (!KINDS.includes(kind as NodeAssistKind)) bad(`某条建议的 kind（${kind}）`)
    // canRun 必须是布尔：缺字段时**不兜底成 false**——那会把「服务端改了字段名」变成
    // 「所有建议突然都不能点了」，而那种坏法没人会归因到接口上。
    if (typeof item.canRun !== 'boolean') bad('某条建议的 canRun')
    return {
      id: text(item.id, '某条建议的 id'),
      title: text(item.title, '某条建议的 title'),
      kind: kind as NodeAssistKind,
      skillId: text(item.skillId, '某条建议的 skillId'),
      prompt: text(item.prompt, '某条建议的 prompt'),
      negativePrompt: text(item.negativePrompt, '某条建议的 negativePrompt'),
      blocked: text(item.blocked, '某条建议的 blocked'),
      canRun: item.canRun
    }
  })

  return {
    recordId: text(source.recordId, 'recordId'),
    summary: text(source.summary, 'summary'),
    hasUpstream: source.hasUpstream === true,
    suggestions
  }
}

/** 点下去做什么。`none` = 摆出来但没接执行方（灰着，原因写在 reason 上）。 */
export type NodeMenuAction = 'copyPrompt' | 'runImage' | 'editNode' | 'deleteNode' | 'none'

/** 菜单分组：只影响渲染时在哪切一条分隔线。 */
export type NodeMenuGroup = 'summary' | 'assist' | 'node'

export type NodeMenuItem = {
  id: string
  label: string
  group: NodeMenuGroup
  enabled: boolean
  /** 灰着的原因（显示在标题上）。能点就没有它。 */
  reason?: string
  action: NodeMenuAction
}

/**
 * 网页端**现在**有的执行方。这一层刻意是参数而不是写死的：
 * 服务端一旦接上 Agent 执行方，改的是调用处传进来的这个对象，不是菜单的拼装规则。
 */
export type NodeMenuCapabilities = {
  /** 出图那条技能的 ID（来自 `/api/web/skills`）；没有 = 服务端没预授权或没配模型。 */
  imageSkillId: string | null
}

/**
 * 网页端只摆出来、但还跑不了的那几条（原因写清楚，不要让人以为菜单坏了）。
 * 这几句是**网页端专有**的：桌面端有 Agent 与站点池子，所以它们不进两端共读的 uiText.json。
 */
export const WEB_ONLY_REASONS = {
  agent: '网页端还没有 Agent 执行方：这一步请在桌面端做',
  video: '出视频执行方尚未接入',
  noImageSkill: '服务端没有可用的本机图像技能（未预授权或没配模型）'
}

/**
 * 计划 → 菜单项。
 *
 * 顺序照着桌面端那份菜单：先一行「看到了什么」（灰的），再是各条建议，最后是节点操作。
 * 标签直接用服务端给的 `title`——两端看到的是同一句话，不再各写一份。
 */
export function buildNodeMenu(plan: NodeAssistPlan, capabilities: NodeMenuCapabilities): NodeMenuItem[] {
  const items: NodeMenuItem[] = [{ id: 'summary', label: plan.summary, group: 'summary', enabled: false, action: 'none' }]

  for (const suggestion of plan.suggestions) {
    // 先看服务端怎么说（节点没内容又没素材这类），再看网页端自己有没有执行方。
    const reason = !suggestion.canRun
      ? suggestion.blocked
      : suggestion.kind === 'Agent'
        ? WEB_ONLY_REASONS.agent
        : suggestion.kind === 'Video'
          ? WEB_ONLY_REASONS.video
          : suggestion.kind === 'Image' && capabilities.imageSkillId === null
            ? WEB_ONLY_REASONS.noImageSkill
            : undefined

    // 灰着的项一律 `none`：它现在**没有**动作。留一个「点了会跑」的 action 在那儿，
    // 等于给以后留了一条「把不可点的项接上执行方」的错线。
    const action: NodeMenuAction = reason !== undefined
      ? 'none'
      : suggestion.kind === 'Prompt' ? 'copyPrompt'
        : suggestion.kind === 'Image' ? 'runImage' : 'none'

    items.push({
      id: suggestion.id,
      label: suggestion.title,
      group: 'assist',
      enabled: reason === undefined,
      ...(reason === undefined ? {} : { reason }),
      action
    })
  }

  // 节点操作那两个标签两端都有（桌面端 MainWindow 右键里是同一个词），所以走共享文案。
  items.push({ id: 'editNode', label: uiText('nodeMenu.edit'), group: 'node', enabled: true, action: 'editNode' })
  items.push({ id: 'deleteNode', label: uiText('nodeMenu.delete'), group: 'node', enabled: true, action: 'deleteNode' })
  return items
}
