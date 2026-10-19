import type { WebSkill } from './AgentPanel'

/**
 * 右键出图的「出图方式」：图片 API、本机 ComfyUI、已安装工作流都是 TextToImage 技能，
 * 用户选一次就记在这台浏览器上。默认（没选过）仍取服务端目录里的第一项，
 * 也就是原来的行为——升级上来不会突然换后端。
 */

export const IMAGE_SKILL_PREF_KEY = 'yeeeyee.web.imageSkill'

type PreferenceStore = Pick<Storage, 'getItem' | 'setItem'>

function browserStore(): PreferenceStore | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage
  } catch {
    return null
  }
}

export function readImageSkillPreference(store: PreferenceStore | null = browserStore()): string {
  try {
    return (store?.getItem(IMAGE_SKILL_PREF_KEY) ?? '').trim()
  } catch {
    return ''
  }
}

export function writeImageSkillPreference(id: string, store: PreferenceStore | null = browserStore()): void {
  try {
    if (id.trim().length === 0) return
    store?.setItem(IMAGE_SKILL_PREF_KEY, id)
  } catch {
    // 浏览器禁用存储时只活这一次会话；出图本身不依赖这个偏好。
  }
}

/** 所有能出图的技能（不区分后端）。 */
export function imageSkills(skills: WebSkill[]): WebSkill[] {
  return skills.filter(skill => skill.capability === 'TextToImage')
}

/**
 * 选中当前出图技能：偏好还在就用偏好；技能被删/停用就退回目录第一项；
 * 一条都没有时返回 null（菜单会显示「没有可调用的文生图技能」）。
 */
export function selectImageSkill(skills: WebSkill[], preferredId: string): WebSkill | null {
  const available = imageSkills(skills)
  const preferred = preferredId.trim()
  return (preferred.length > 0 ? available.find(skill => skill.id === preferred) : undefined) ?? available[0] ?? null
}

/** 已安装工作流技能：请求体只接受 prompt/idempotencyKey/targetNodeId(/candidateBatch)，没有比例。 */
export function isWorkflowImageSkill(skill: WebSkill): boolean {
  return skill.id.startsWith('comfyui.workflow.')
}
