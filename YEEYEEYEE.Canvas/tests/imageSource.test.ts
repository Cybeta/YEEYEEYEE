import { describe, expect, it } from 'vitest'
import type { WebSkill } from '../src/shell/AgentPanel'
import {
  IMAGE_SKILL_PREF_KEY,
  imageSkills,
  isWorkflowImageSkill,
  readImageSkillPreference,
  selectImageSkill,
  writeImageSkillPreference
} from '../src/shell/imageSource'

/**
 * 出图方式选择：偏好还在就用偏好，技能消失退回目录第一项，绝不把非图片技能混进来。
 * 这三件事错了只会表现成「菜单里少一项」或「点下去报了另一个后端」，靠界面看不出来。
 */

const skill = (id: string, capability = 'TextToImage'): WebSkill => ({ id, name: id, capability })

const skills: WebSkill[] = [
  skill('web.text-to-image'),
  skill('comfyui.text-to-image'),
  skill('comfyui.workflow.abc'),
  skill('web.text-to-video', 'TextToVideo')
]

function memoryStore(initial: Record<string, string> = {}) {
  const data = { ...initial }
  return {
    data,
    getItem: (key: string) => data[key] ?? null,
    setItem: (key: string, value: string) => { data[key] = value }
  }
}

describe('imageSource', () => {
  it('只把 TextToImage 视为出图技能', () => {
    expect(imageSkills(skills).map(item => item.id)).toEqual([
      'web.text-to-image',
      'comfyui.text-to-image',
      'comfyui.workflow.abc'
    ])
  })

  it('偏好存在且仍可用时用它', () => {
    expect(selectImageSkill(skills, 'comfyui.text-to-image')?.id).toBe('comfyui.text-to-image')
  })

  it('偏好指向已删除的技能时退回目录第一项', () => {
    expect(selectImageSkill(skills, 'comfyui.workflow.gone')?.id).toBe('web.text-to-image')
  })

  it('没有任何出图技能时返回 null', () => {
    expect(selectImageSkill([skill('web.text-to-video', 'TextToVideo')], 'web.text-to-image')).toBeNull()
  })

  it('偏好按 key 写入，空白值不覆盖已有选择', () => {
    const store = memoryStore({ [IMAGE_SKILL_PREF_KEY]: 'web.text-to-image' })
    writeImageSkillPreference('comfyui.text-to-image', store)
    expect(store.data[IMAGE_SKILL_PREF_KEY]).toBe('comfyui.text-to-image')
    expect(readImageSkillPreference(store)).toBe('comfyui.text-to-image')
    writeImageSkillPreference('   ', store)
    expect(readImageSkillPreference(store)).toBe('comfyui.text-to-image')
  })

  it('工作流技能按 comfyui.workflow. 前缀识别', () => {
    expect(isWorkflowImageSkill(skill('comfyui.workflow.abc'))).toBe(true)
    expect(isWorkflowImageSkill(skill('comfyui.text-to-image'))).toBe(false)
    expect(isWorkflowImageSkill(skill('web.text-to-image'))).toBe(false)
  })
})
