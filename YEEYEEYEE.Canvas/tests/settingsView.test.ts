import { describe, expect, it } from 'vitest'
import { applyModelPreset } from '../src/shell/ModelSettings'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { SettingsPanel } from '../src/shell/SettingsPanel'
import { parseSettings, settingsGroups, parseComfyUiProbe, describeComfyUiProbe } from '../src/shell/settingsView'

/**
 * 网页端设置页（第 179 轮）。
 *
 * 这一组测试盯两件事：
 * ① **密钥永不出现在界面那一侧**——就算服务端哪天不小心把它塞进响应，这一层也不带它进去；
 * ② 没配的东西写成「未配置」并标出来，不是留一格空白（空白会被读成「配了但没显示」）。
 */
const configured = {
  configured: true,
  selectedProfileId: 'p1',
  active: { endpoint: 'https://api.example.com/v1', model: 'gpt-x', apiFormat: '1', hasKey: true },
  providers: [
    { id: 'p1', displayName: '主力', endpoint: 'https://api.example.com/v1', model: 'gpt-x', enabled: true, hasKey: true },
    { id: 'p2', displayName: '备用', endpoint: '', model: '', enabled: false, hasKey: false }
  ],
  media: { imageEndpoint: '', imageModel: '', imageHasKey: false, comfyUiBaseUrl: 'http://127.0.0.1:8188', comfyUiCheckpoint: 'sd_xl', videoEndpoint: '', videoModel: '' },
  collaboration: { serverUrl: 'http://localhost:5000', account: 'cybeta', autoSync: false },
  disabledSkills: ['a', 'b']
}

it('设置初始分页显示大模型，媒体页面保持挂载但隐藏', () => {
  const html = renderToStaticMarkup(createElement(SettingsPanel))
  expect(html).toContain('role="tablist" aria-label="设置分页"')
  expect(html).toContain('aria-selected="true" aria-controls="settings-models"')
  expect(html).toContain('id="settings-media" role="tabpanel" aria-labelledby="settings-media-tab" hidden=""')
  expect(html).toContain('生图与生视频')
  expect(html).toContain('id="settings-skills" role="tabpanel" aria-labelledby="settings-skills-tab" hidden=""')
  expect(html).toContain('id="settings-collaboration" role="tabpanel" aria-labelledby="settings-collaboration-tab" hidden=""')
  expect(html.indexOf('>模型接入</button>')).toBeLessThan(html.indexOf('>生图与生视频</button>'))
  expect(html.indexOf('>生图与生视频</button>')).toBeLessThan(html.indexOf('>技能管理</button>'))
  expect(html.indexOf('>技能管理</button>')).toBeLessThan(html.indexOf('>协作</button>'))
})

describe('模型预设', () => {
  const draft = { id: 'saved', displayName: '我的配置', endpoint: 'https://custom.example/v1', model: 'manual', apiFormat: 1, useFullUrl: true, enabled: false, hasKey: true, presetId: 'custom', contextWindow: 10, maxOutputTokens: 20, sendSamplingParameters: true, supportsImageInput: false }
  const values = { endpoint: 'https://preset.example/v1', format: 0, useFullUrl: false, modelId: 'recommended', contextWindow: 1000, maxOutputTokens: 200, sendsSamplingParameters: false, supportsVision: true }
  const preset = { id: 'provider', name: '厂家', hint: '', manualCapabilities: false, defaults: values, models: [] }
  it('应用服务端能力默认值，同时保留配置身份与密钥状态', () => {
    expect(applyModelPreset(draft, preset)).toEqual({ ...draft, presetId: 'provider', endpoint: values.endpoint, apiFormat: 0, useFullUrl: false, model: 'recommended', contextWindow: 1000, maxOutputTokens: 200, sendSamplingParameters: false, supportsImageInput: true })
  })
  it('新增配置选择服务商时填入默认显示名称', () => {
    expect(applyModelPreset({ ...draft, displayName: '' }, preset).displayName).toBe('厂家')
  })
  it('切回自定义保留已填字段；无预置型号时保留手填能力', () => {
    expect(applyModelPreset(draft, { ...preset, id: 'custom' })).toEqual(draft)
    expect(applyModelPreset(draft, { ...preset, manualCapabilities: true, defaults: { ...values, modelId: '' } })).toMatchObject({ model: '', contextWindow: 10, maxOutputTokens: 20, supportsImageInput: false, hasKey: true })
  })
})

describe('设置响应', () => {
  it('形状不对就抛——页面上一格空白不会报错，只会让人以为没配', () => {
    expect(() => parseSettings(null)).toThrow(/不对/)
    expect(() => parseSettings({ configured: 'yes' })).toThrow(/configured/)
    expect(() => parseSettings({ configured: true, active: {}, media: {}, collaboration: {} })).toThrow(/providers/)
  })

  it('密钥永远不带进界面这一侧（服务端就算误发，这一层也不认）', () => {
    const leaky = {
      ...configured,
      apiKey: 'test-api-key',
      imageApiKey: 'test-image-key',
      providers: [{ ...configured.providers[0], apiKey: 'test-profile-key' }]
    }
    const parsed = parseSettings(leaky)
    expect(JSON.stringify(parsed)).not.toContain('sk-')
    // 「配没配」这个答案是留着的——那才是界面要显示的东西。
    expect(parsed.active.hasKey).toBe(true)
  })

  it('没有配置文件时给一句能照做的话，而不是三个空分组', () => {
    const parsed = parseSettings({ configured: false, note: '这台机器上还没有模型配置文件' })
    expect(parsed.configured).toBe(false)
    const groups = settingsGroups(parsed)
    expect(groups).toHaveLength(1)
    expect(groups[0].rows[0]).toMatchObject({ value: '这台机器上还没有模型配置文件', warn: true })
  })

  it('分组与兜底：没配的写「未配置」并标出来，密钥只说配没配', () => {
    const groups = settingsGroups(parseSettings(configured))
    expect(groups.map((group) => group.title)).toEqual(['模型接入', '生图与生视频', '技能管理', '协作'])

    const model = groups[0].rows
    expect(model.find((row) => row.label === '当前端点')).toMatchObject({ value: 'https://api.example.com/v1' })
    expect(model.find((row) => row.label === '当前端点')?.warn).toBeUndefined()
    expect(model.find((row) => row.label === '密钥')).toMatchObject({ value: '已配置' })
    expect(model.find((row) => row.label === '备用接入点')).toMatchObject({ value: '1 个已启用 / 共 2 个' })

    const media = groups[1].rows
    expect(media.find((row) => row.label === '生图端点')).toMatchObject({ value: '未配置', warn: true })
    expect(media.find((row) => row.label === '密钥')).toMatchObject({ value: '未配置', warn: true })
    expect(media.find((row) => row.label === 'ComfyUI 模型')).toMatchObject({ value: 'sd_xl' })

    expect(groups[2].rows.find((row) => row.label === '停用的内置技能')).toMatchObject({ value: 'a、b' })
    const collaboration = groups[3].rows
    expect(collaboration.find((row) => row.label === '改完自动同步')).toMatchObject({ value: '关' })
    expect(collaboration.find((row) => row.label === '停用的内置技能')).toBeUndefined()
  })
})

describe('ComfyUI 探针', () => {
  it('通了就报地址、版本与设备', () => {
    const probe = parseComfyUiProbe({ reachable: true, baseUrl: 'http://127.0.0.1:8188/', version: '0.37.0', device: 'RTX 4080 SUPER', detail: '已连接' })
    expect(describeComfyUiProbe(probe)).toBe('通：http://127.0.0.1:8188/（版本 0.37.0，设备 RTX 4080 SUPER）')
  })

  it('不通时把服务端那句原话带上——「404 后面是一页 HTML」与「连接被拒绝」要修的是两件事', () => {
    const probe = parseComfyUiProbe({ reachable: false, baseUrl: 'http://x:8443/', detail: 'HTTP 404：<!DOCTYPE html>…' })
    expect(describeComfyUiProbe(probe)).toContain('HTTP 404：<!DOCTYPE html>')
  })

  it('服务端没给 detail 时也不能只说「失败」两个字', () => {
    const probe = parseComfyUiProbe({ reachable: false, baseUrl: '' })
    expect(describeComfyUiProbe(probe)).toBe('不通：后端没有回应')
  })

  it('形状不对就抛，不把 undefined 画到页面上', () => {
    expect(() => parseComfyUiProbe(null)).toThrow(/reachable/)
    expect(() => parseComfyUiProbe({ reachable: 'yes' })).toThrow(/reachable/)
  })
})
