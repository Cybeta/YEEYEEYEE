import { describe, expect, it } from 'vitest'
import {
  arrayParameterValue,
  attachmentAccept,
  attachmentMaxBytes,
  attachmentPreviewMode,
  buildParameterPayload,
  decodeEntityReference,
  editableParameters,
  encodeEntityReference,
  entityReferenceKindLabel,
  entityReferenceLabel,
  entityReferenceOptions,
  formatBytes,
  initialParameterValues,
  parseParameterInput,
  parseSkillCatalog,
  referenceAttachmentChoices,
  skillDeclaration,
  toggleArrayParameter,
  type ChatParameterSpec
} from '../src/shell/chatSkillParams'

const spec = (over: Partial<ChatParameterSpec>): ChatParameterSpec => ({
  name: 'value', kind: 'string', required: false, description: '', options: [], editable: true,
  minimum: null, maximum: null, ...over
})

describe('技能参数声明解析', () => {
  it('读出服务端下发的声明与附件类型；读不懂的项跳过而不是猜', () => {
    const catalog = parseSkillCatalog({
      skills: [
        { id: 'web.text-to-image', name: '图片', parameters: [
          { name: 'aspectRatio', kind: 'enum', required: false, description: '比例', options: ['1:1'], editable: true },
          { name: 'weird', kind: 'fancy' },
          { kind: 'string' }
        ] },
        { name: 'missing-id' }
      ],
      attachments: [
        { mime: 'image/png', kind: 'Image', maxBytes: 5242880, referenceable: true },
        { kind: 'Image' }
      ],
      attachmentAccept: 'image/png'
    })
    expect(catalog.skills.map(skill => skill.id)).toEqual(['web.text-to-image'])
    expect(catalog.skills[0].parameters.map(parameter => parameter.name)).toEqual(['aspectRatio'])
    expect(catalog.attachmentTypes.map(type => type.mime)).toEqual(['image/png'])
    expect(attachmentAccept(catalog)).toBe('image/png')
    expect(attachmentMaxBytes(catalog, 'image/png')).toBe(5242880)
    // 没有声明的技能与类型：不编，回落到默认值。
    expect(skillDeclaration(catalog, 'unknown.skill')).toEqual([])
    expect(attachmentMaxBytes(catalog, 'video/mp4')).toBe(0)
  })

  it('取不到声明时 accept 退化为旧的可选类型', () => {
    expect(attachmentAccept(null)).toBe('image/png,image/jpeg,text/plain')
  })
})

describe('技能参数编辑', () => {
  it('只有声明为 editable 的参数进编辑器', () => {
    const specs = [spec({ name: 'a', editable: true }), spec({ name: 'b', editable: false })]
    expect(editableParameters(specs).map(item => item.name)).toEqual(['a'])
  })

  it('初值照抄模型给的值；没给就留空——不替用户默认枚举', () => {
    const values = initialParameterValues(
      [spec({ name: 'aspectRatio', kind: 'enum', options: ['1:1', '16:9'] }), spec({ name: 'negativePrompt' })],
      { negativePrompt: '模糊' })
    expect(values).toEqual({ aspectRatio: '', negativePrompt: '模糊' })
  })

  it('六种类型都能折回 JSON 值', () => {
    expect(parseParameterInput(spec({ kind: 'string' }), ' 灯笼 ')).toEqual({ ok: true, value: '灯笼' })
    expect(parseParameterInput(spec({ kind: 'number', minimum: 1, maximum: 600 }), '15')).toEqual({ ok: true, value: 15 })
    expect(parseParameterInput(spec({ kind: 'boolean' }), 'true')).toEqual({ ok: true, value: true })
    expect(parseParameterInput(spec({ kind: 'enum', options: ['16:9', '9:16'] }), '9:16')).toEqual({ ok: true, value: '9:16' })
    expect(parseParameterInput(spec({ kind: 'array' }), '["a","b"]')).toEqual({ ok: true, value: ['a', 'b'] })
    expect(parseParameterInput(spec({ kind: 'object' }), '{"k":"v","n":1,"b":true}')).toEqual({ ok: true, value: { k: 'v', n: 1, b: true } })
  })

  it('类型不符就明确报错，不做静默取整或强转', () => {
    expect(parseParameterInput(spec({ kind: 'number', minimum: 1, maximum: 600 }), '3.7').ok).toBe(false)
    expect(parseParameterInput(spec({ kind: 'number', minimum: 1, maximum: 600 }), '601').ok).toBe(false)
    expect(parseParameterInput(spec({ kind: 'enum', options: ['16:9'] }), '4:3').ok).toBe(false)
    expect(parseParameterInput(spec({ kind: 'array' }), '[1]').ok).toBe(false)
    expect(parseParameterInput(spec({ kind: 'object' }), '{"k":{"deep":1}}').ok).toBe(false)
    expect(parseParameterInput(spec({ kind: 'object' }), 'not-json').ok).toBe(false)
  })

  it('提交时只带用户确实填了的参数；必填留空才报错', () => {
    const specs = [
      spec({ name: 'negativePrompt' }),
      spec({ name: 'aspectRatio', kind: 'enum', options: ['1:1'] }),
      spec({ name: 'seed', kind: 'number', required: true, minimum: 0, maximum: 100 })
    ]
    // 选填项没填就不提交：把空值也发上去，等于把「我没动它」说成「我要求它是空」。
    const chosen = buildParameterPayload(specs, { aspectRatio: '1:1', seed: '42' })
    expect(chosen.errors).toEqual([])
    expect(chosen.parameters).toEqual({ aspectRatio: '1:1', seed: 42 })

    const missing = buildParameterPayload(specs, { aspectRatio: '1:1' })
    expect(missing.errors).toEqual(['缺少必填参数 seed'])
    expect(missing.parameters).toEqual({ aspectRatio: '1:1' })

    const invalid = buildParameterPayload(specs, { seed: 'abc' })
    expect(invalid.errors).toEqual(['seed 必须是整数'])
    expect(invalid.parameters).toEqual({})
  })
})

describe('数组参数（参考图附件）', () => {
  const arraySpec: ChatParameterSpec = spec({ name: 'referenceAttachmentIds', kind: 'array' })

  it('候选来自会话里的图片附件；名字缺失时回落到 ID', () => {
    expect(referenceAttachmentChoices([{ id: 'a1', name: '参考.png' }, { id: 'a2', name: '' }]))
      .toEqual([{ value: 'a1', label: '参考.png' }, { value: 'a2', label: 'a2' }])
  })

  it('取值与切换都以 JSON 文本为准；读不出来当空，不猜也不抛', () => {
    expect(arrayParameterValue('["a1","a2"]')).toEqual(['a1', 'a2'])
    expect(arrayParameterValue('[]')).toEqual([])
    expect(arrayParameterValue('')).toEqual([])
    expect(arrayParameterValue('not-json')).toEqual([])
    // 混进非字符串项时只留字符串项：整段 JSON 已经解析成功，丢掉它反而会让用户已选的图消失。
    expect(arrayParameterValue('["a1",3]')).toEqual(['a1'])
    expect(toggleArrayParameter('', 'a1')).toBe('["a1"]')
    expect(toggleArrayParameter('["a1"]', 'a2')).toBe('["a1","a2"]')
    expect(toggleArrayParameter('["a1","a2"]', 'a1')).toBe('["a2"]')
  })

  it('空数组与未填写是两种输入：空数组提交成「明确不要参考图」', () => {
    expect(buildParameterPayload([arraySpec], { referenceAttachmentIds: '[]' }).parameters).toEqual({ referenceAttachmentIds: [] })
    expect(buildParameterPayload([arraySpec], { referenceAttachmentIds: '' }).parameters).toEqual({})
  })
})

describe('实体引用', () => {
  it('选项值能原样拆回（往返一致），缺类型或 ID 就认 null', () => {
    const reference = { kind: 'character', id: 'e1', variantId: 'v1', versionId: 'r1' }
    expect(decodeEntityReference(encodeEntityReference(reference))).toEqual(reference)
    expect(decodeEntityReference('character|e1')).toEqual({ kind: 'character', id: 'e1', variantId: undefined, versionId: undefined })
    expect(decodeEntityReference('|e1')).toBeNull()
    expect(decodeEntityReference('character|')).toBeNull()
  })

  it('把画布节点与资源库实体摊成可挑条目；认不出种类的实体不放进选项', () => {
    const options = entityReferenceOptions(
      [{ id: 'node-1', title: '第一镜' }],
      [
        { id: 'e1', name: '林夏', kind: 'Character', variants: [{ id: 'v1', name: '默认' }] },
        { id: 'e2', name: '旧书店', kind: 'Scene' },
        { id: 'e3', name: '谜之实体', kind: 'Unknown' }
      ])
    expect(options.map(option => option.label)).toEqual([
      '项目节点 · 第一镜', '人物 · 林夏 · 默认', '场景 · 旧书店'
    ])
    expect(options[1].reference).toEqual({ kind: 'character', id: 'e1', variantId: 'v1' })
    // 没有变体的实体也给一行：服务端会落到它的第一个变体。
    expect(options[2].reference).toEqual({ kind: 'scene', id: 'e2' })
  })

  it('引用摘要用服务端回填的名字与版本；缺名字回落到 ID，不编', () => {
    expect(entityReferenceLabel({ kind: 'prop', id: 'p1', name: '黄铜钥匙', variantName: '默认', version: 'v2' }))
      .toBe('道具「黄铜钥匙」 · 默认 · v2')
    expect(entityReferenceLabel({ kind: 'node', id: 'n1' })).toBe('项目节点「n1」')
    expect(entityReferenceKindLabel('weird')).toBe('weird')
  })
})

describe('附件展示', () => {
  it('按种类决定预览方式；文本与文档走链接降级', () => {
    expect(attachmentPreviewMode('Image')).toBe('image')
    expect(attachmentPreviewMode('Video')).toBe('video')
    expect(attachmentPreviewMode('Audio')).toBe('audio')
    expect(attachmentPreviewMode('Text')).toBe('link')
    expect(attachmentPreviewMode('Document')).toBe('link')
    expect(attachmentPreviewMode('Other')).toBe('link')
  })

  it('大小上限文案：MiB 与 KiB 各按自己的量级', () => {
    expect(formatBytes(5242880)).toBe('5MiB')
    expect(formatBytes(65536)).toBe('64KiB')
    expect(formatBytes(0)).toBe('')
  })
})
