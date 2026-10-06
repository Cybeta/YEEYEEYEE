import { describe, expect, it } from 'vitest'
import { parseSettings, settingsGroups } from '../src/shell/settingsView'

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
    expect(groups.map((group) => group.title)).toEqual(['模型接入', '生图与生视频', '协作与技能'])

    const model = groups[0].rows
    expect(model.find((row) => row.label === '当前端点')).toMatchObject({ value: 'https://api.example.com/v1' })
    expect(model.find((row) => row.label === '当前端点')?.warn).toBeUndefined()
    expect(model.find((row) => row.label === '密钥')).toMatchObject({ value: '已配置' })
    expect(model.find((row) => row.label === '备用接入点')).toMatchObject({ value: '1 个已启用 / 共 2 个' })

    const media = groups[1].rows
    expect(media.find((row) => row.label === '生图端点')).toMatchObject({ value: '未配置', warn: true })
    expect(media.find((row) => row.label === '密钥')).toMatchObject({ value: '未配置', warn: true })
    expect(media.find((row) => row.label === 'ComfyUI 模型')).toMatchObject({ value: 'sd_xl' })

    const collaboration = groups[2].rows
    expect(collaboration.find((row) => row.label === '改完自动同步')).toMatchObject({ value: '关' })
    expect(collaboration.find((row) => row.label === '停用的内置技能')).toMatchObject({ value: 'a、b' })
  })
})
