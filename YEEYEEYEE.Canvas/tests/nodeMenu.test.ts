import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { buildNodeMenu, parseNodeAssist, PRODUCT_VIDEO_SUGGESTION_ID, WEB_ONLY_REASONS, type NodeAssistPlan, type NodeAssistSuggestion } from '../src/shell/nodeMenu'
import { uiText } from '../src/shell/uiText'

/**
 * 网页端节点右键菜单（第 174 轮）。
 *
 * 这一组测试盯的是**两条规则**，而不是「菜单长什么样」：
 *   ① 菜单里有哪些建议由服务端那份计划决定（与桌面端同一份来源），网页端只翻译「这一端能不能跑」；
 *   ② 凡是灰着的项**必须带原因**——灰着不说话，用户只会读成「菜单坏了」。
 * 第 ② 条是这轮最值钱的一条：它是一条不变量，以后加菜单项时不会悄悄漏掉原因。
 */

/** 一条「能跑」的建议。 */
function suggestion(overrides: Partial<NodeAssistSuggestion> = {}): NodeAssistSuggestion {
  return { ...baseSuggestion(), ...overrides }
}

function baseSuggestion(): NodeAssistSuggestion {
  return {
    id: 'character-front',
    title: '出角色图（正面全身）',
    kind: 'Image',
    skillId: 'character-front',
    prompt: '正面全身，站姿',
    negativePrompt: '',
    blocked: '',
    canRun: true
  }
}

function plan(suggestions: ReturnType<typeof baseSuggestion>[]): NodeAssistPlan {
  return { recordId: 'n1', summary: '看到：角色卡「林晚」', hasUpstream: false, suggestions }
}

const ready = { imageSkillId: 'comfyui.text-to-image', videoSkillId: 'web.text-to-video', canRunAgent: true }
const noSkill = { imageSkillId: null, videoSkillId: null, canRunAgent: false }
const noVideo = { ...ready, videoSkillId: null }

describe('节点右键菜单：把服务端的协助计划翻成这一端能点的项', () => {
  it('第一行是「看到了什么」（灰的），最后两项是节点操作（两端同一个词）', () => {
    const items = buildNodeMenu(plan([suggestion()]), ready)
    expect(items[0]).toMatchObject({ id: 'summary', group: 'summary', enabled: false, action: 'none' })
    expect(items[0].label).toBe('看到：角色卡「林晚」')

    expect(items.at(-2)).toMatchObject({ id: 'editNode', label: uiText('nodeMenu.edit'), enabled: true, action: 'editNode' })
    expect(items.at(-1)).toMatchObject({ id: 'deleteNode', label: uiText('nodeMenu.delete'), enabled: true, action: 'deleteNode' })
  })

  it('标签直接用服务端给的 title——两端看到的是同一句话，不在这一侧另写一份', () => {
    const items = buildNodeMenu(plan([suggestion({ id: 'a', title: '出场景基准图（远景宽幅）' })]), ready)
    expect(items.map((item) => item.label)).toContain('出场景基准图（远景宽幅）')
  })

  it('提示词类：能跑就可点，点下去是「复制提示词」', () => {
    const items = buildNodeMenu(plan([suggestion({ id: 'p', kind: 'Prompt', title: '生成角色设定提示词（外观锚点）' })]), ready)
    const item = items.find((entry) => entry.id === 'p')!
    expect(item).toMatchObject({ enabled: true, action: 'copyPrompt' })
    expect(item.reason).toBeUndefined()
  })

  it('出图类：服务端有那条技能才可点；没有就灰着并说明为什么', () => {
    const items = buildNodeMenu(plan([suggestion({ id: 'i', kind: 'Image' })]), ready)
    expect(items.find((entry) => entry.id === 'i')).toMatchObject({ enabled: true, action: 'runImage' })

    const without = buildNodeMenu(plan([suggestion({ id: 'i', kind: 'Image' })]), noSkill)
    expect(without.find((entry) => entry.id === 'i')).toMatchObject({
      enabled: false,
      action: 'none',
      reason: WEB_ONLY_REASONS.noImageSkill
    })
  })

  it('Agent 有编辑权限即可执行；出视频有视频技能才可点，没有就灰着说明原因', () => {
    const suggestions = [suggestion({ id: 'a', kind: 'Agent', title: '让 Agent 补全角色小传' }), suggestion({ id: 'v', kind: 'Video', title: '出这一镜的视频' })]
    const items = buildNodeMenu(plan(suggestions), ready)
    expect(items.find(entry => entry.id === 'a')).toMatchObject({ enabled: true, action: 'runAgent' })
    expect(items.find(entry => entry.id === 'v')).toMatchObject({ enabled: true, action: 'runVideo' })
    expect(buildNodeMenu(plan(suggestions), noVideo).find(entry => entry.id === 'v'))
      .toMatchObject({ enabled: false, action: 'none', reason: WEB_ONLY_REASONS.noVideoSkill })
    expect(buildNodeMenu(plan(suggestions), noSkill).find(entry => entry.id === 'a')).toMatchObject({ enabled: false, action: 'none', reason: WEB_ONLY_REASONS.agent })
    expect(buildNodeMenu(plan([suggestion({ id: 'a', kind: 'Agent', canRun: false, blocked: '缺少正文' })]), ready).find(entry => entry.id === 'a')).toMatchObject({ enabled: false, action: 'none', reason: '缺少正文' })
  })

  it('服务端说这条视频跑不了（没素材 / 这一端链路没配好）：用服务端那句，与有没有技能无关', () => {
    const blocked = '还没有可用的出视频链路：请在「设置 → 生图与生视频 → 视频接口」里填上地址与模型。'
    const items = buildNodeMenu(plan([suggestion({ id: 'v', kind: 'Video', canRun: false, blocked })]), ready)
    expect(items.find(entry => entry.id === 'v')).toMatchObject({ enabled: false, action: 'none', reason: blocked })
  })

  it('「成片」那条不在菜单里跑：计划会把它标成能跑，但网页端的入口在成品卡的成片面板', () => {
    // NodeAssist 的 Product 分支故意不拦它（桌面端点开是个对话框，逐镜看附件）。
    const film = suggestion({ id: PRODUCT_VIDEO_SUGGESTION_ID, kind: 'Video', title: '出这一版的成片视频', canRun: true, blocked: '' })
    const item = buildNodeMenu(plan([film]), ready).find(entry => entry.id === PRODUCT_VIDEO_SUGGESTION_ID)!
    expect(item).toMatchObject({ enabled: false, action: 'none', reason: WEB_ONLY_REASONS.filmInstead })
  })

  it('出视频提交：目标节点在按下那一刻固定，且不进图片候选卡那条路', () => {
    const app = readFileSync(new URL('../src/WebCanvasApp.tsx', import.meta.url), 'utf8')
    // 目标取菜单当时那个节点（不是「当前选中」），点击之后不再重新解析目标。
    expect(app).toContain('await submitNodeVideo(videoSkill, suggestion.prompt, menu.recordId)')
    expect(app).toContain('targetNodeId: nodeId')
    // 视频一次就是一版：提交体里**没有** candidateBatch 这个字段，产物由服务端成功后直接回挂原节点。
    const submit = app.slice(app.indexOf('async function submitNodeVideo'), app.indexOf('async function runImageBatch'))
    expect(submit).toContain('targetNodeId: nodeId')
    expect(submit).not.toMatch(/candidateBatch:/)
  })

  it('服务端说跑不了时，用服务端给的原因（两端同一句），不看网页端有没有执行方', () => {
    const blocked = '这个节点还没写内容，也没有引用或上游设定；先写点内容、挂一条引用，或者连一条上游节点，再来让 Agent 协助'
    const items = buildNodeMenu(plan([suggestion({ id: 'b', canRun: false, blocked })]), ready)
    expect(items.find((entry) => entry.id === 'b')).toMatchObject({ enabled: false, reason: blocked })
  })

  it('不变量：凡是灰着的项都要有原因——灰着不说话只会被读成菜单坏了', () => {
    const items = buildNodeMenu(plan([
      suggestion({ id: 'ok' }),
      suggestion({ id: 'agent', kind: 'Agent' }),
      suggestion({ id: 'video', kind: 'Video' }),
      suggestion({ id: 'blocked', canRun: false, blocked: '没内容' })
    ]), noSkill)
    // 扫到 0 个灰项说明这条不变量在空转。第一行「看到了什么」是**表头**不是动作项，不算在内。
    const disabled = items.filter((item) => !item.enabled && item.group !== 'summary')
    expect(disabled.length).toBeGreaterThan(2)
    for (const item of disabled) expect(item.reason, item.id).toBeTruthy()
    // 可点的项不该带原因（带了说明翻译那一步写反了）。
    for (const item of items.filter((entry) => entry.enabled)) expect(item.reason, item.id).toBeUndefined()
  })
})

describe('节点协助计划的响应解析', () => {
  it('形状对就原样读出来', () => {
    const parsed = parseNodeAssist({
      recordId: 'n1', summary: '看到：角色卡', hasUpstream: true,
      suggestions: [{ id: 'a', title: 't', kind: 'Prompt', skillId: 's', prompt: 'p', negativePrompt: '', blocked: '', canRun: true }]
    })
    expect(parsed.suggestions[0]).toMatchObject({ id: 'a', kind: 'Prompt', canRun: true })
    expect(parsed.hasUpstream).toBe(true)
  })

  it('形状不对就当场炸——少几项没人会发现，只会觉得这个功能很弱', () => {
    expect(() => parseNodeAssist(null)).toThrow(/不对/)
    expect(() => parseNodeAssist({ recordId: 'n1', summary: 's' })).toThrow(/suggestions/)
    expect(() => parseNodeAssist({
      recordId: 'n1', summary: 's', suggestions: [{ id: 'a', title: 't', kind: 'Nope', skillId: '', prompt: '', negativePrompt: '', blocked: '', canRun: true }]
    })).toThrow(/kind/)
    expect(() => parseNodeAssist({
      recordId: 'n1', summary: 's', suggestions: [{ id: 'a', title: 't', kind: 'Image', skillId: '', prompt: '', negativePrompt: '', blocked: '' }]
    })).toThrow(/canRun/)
  })
})
