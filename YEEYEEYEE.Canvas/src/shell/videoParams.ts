/**
 * 文生视频这一次「要写什么进去」。
 *
 * 面板上让用户填的是**秒数**与**边长**，但 LTX 那条流水线只认 `videoFrames`（帧数），
 * 秒与帧的换算按工作流里定的 24fps 折——**换算只放在这一处**，面板与请求体都用它，
 * 免得两处各折一次，日后改了帧率只有一边跟着变。
 */

/** 工作流里的帧率（见 ComfyUiWorkflowFactory 里 LTX 的 frame_rate）。 */
export const VIDEO_FRAMES_PER_SECOND = 24

/** LTX 的 video_length 只认 1、9、17、25…（节点 schema 写着 min 1、step 8），合法值之间就差这么多。 */
export const VIDEO_FRAME_STEP = 8

/** 可选边长。做成固定档位，是为了不给用户一个后端会照单全收、跑起来却显存爆掉的数字。 */
export const VIDEO_SIZE_OPTIONS = ['256', '384', '512']

/** 时长上限（秒）。LTX 22B 在这个档位上很慢，给一个能等到的范围。 */
export const VIDEO_MAX_SECONDS = 10

export type VideoParams = {
  seconds: string
  size: string
  seed: string
}

export const DEFAULT_VIDEO_PARAMS: VideoParams = { seconds: '1', size: '256', seed: '' }

export type InvocationBody = {
  prompt: string
  idempotencyKey: string
  seconds?: number
  videoFrames?: number
  width?: number
  height?: number
  seed?: number
}

/**
 * 文生视频这条技能背后的执行器。它决定**这次请求体能带什么**，两者不通用：
 *
 * · `comfyui`：LTX 那条流水线，认 `videoFrames`（由秒折帧）与边长档位。
 * · `http`：接口站那条路（见 WebInvocationExecutor 的 HTTP 分支），只认 `seconds` / `width` / `height`，
 *   而且服务端**明确拒收** `videoFrames` 与 `seed`（没有 ComfyUI 后端时发这些字段一律 400
 *   SKILL_INPUT_UNSUPPORTED——它不肯静默忽略）。画幅也一样：没给宽高时由服务端按模型默认档位决定，
 *   所以这里**不替它猜**，只把秒数发出去。
 *
 * 后端由服务端在 `/api/web/skills` 里如实声明（客户端看不见 `WebVideoBackend` 配置）。
 * 声明缺失时按 `comfyui` 走：那是这条路最早、也是既有测试覆盖的形态，不会把老行为改坏。
 */
export type VideoBackend = 'comfyui' | 'http'

export function videoBackendOf(skill: { videoBackend?: string }): VideoBackend {
  return skill.videoBackend === 'http' ? 'http' : 'comfyui'
}

/** 这个后端下要摆哪些「边长」档位。接口站那条路不发画幅，所以那一档不摆。 */
export function videoSizeOptions(backend: VideoBackend): string[] {
  return backend === 'http' ? [] : VIDEO_SIZE_OPTIONS
}

function toInt(value: string, fallback: number): number {
  const parsed = Number.parseInt(value, 10)
  return Number.isFinite(parsed) ? parsed : fallback
}

export function videoSeconds(value: string): number {
  return Math.min(Math.max(toInt(value, 1), 1), VIDEO_MAX_SECONDS)
}

/**
 * 秒数折成**节点真能接受**的帧数。
 *
 * LTX 的 `video_length` 在自己的 schema 里写着 `min = 1, step = 8`，也就是只认 1、9、17、25…
 * 这一串 1+8k。发一个 24 过去不会报错，它会**悄悄折到下面那个合法值**（24 → 17）：用户点了
 * 1 秒，拿到手的是 0.71 秒，而界面上没有任何地方说这件事。所以这里**向上取**到下一个 1+8k，
 * 宁可多算一帧让时长略长于所求，也不要短给——短了用户只会以为「模型不听话」。
 */
export function videoFramesForSeconds(value: string): number {
  const target = videoSeconds(value) * VIDEO_FRAMES_PER_SECOND
  const steps = Math.ceil((target - 1) / VIDEO_FRAME_STEP)
  return 1 + Math.max(0, steps) * VIDEO_FRAME_STEP
}

/** 折好之后这一段实际有多长（秒）。界面拿它告诉用户「你要 1 秒，这条是 1.04 秒」。 */
export function videoEffectiveSeconds(value: string): number {
  return videoFramesForSeconds(value) / VIDEO_FRAMES_PER_SECOND
}

export function videoSize(value: string): number {
  return VIDEO_SIZE_OPTIONS.includes(value) ? Number.parseInt(value, 10) : 256
}

/**
 * 组装 `/api/web/skills/{id}/invoke` 的请求体。
 *
 * 只有文生视频那条路会带上画面参数：文生图走的是它自己那套（模型、步数），
 * 把它不认识的分辨率参数塞过去，后端只会当成未知字段拒绝整次调用。
 *
 * 视频这两条路的字段**不通用**，所以后端必须由调用方按服务端声明的能力传进来（见 VideoBackend）：
 * 接口站那条路只发秒数；发 `videoFrames`/`seed` 会被自己的服务端以 SKILL_INPUT_UNSUPPORTED 拒收。
 */
export function buildInvocationBody(
  skillId: string,
  prompt: string,
  params: VideoParams,
  idempotencyKey: string,
  backend: VideoBackend = 'comfyui'
): InvocationBody {
  if (skillId !== 'web.text-to-video') return { prompt, idempotencyKey }

  // 接口站那条路：只发秒。帧折算是 LTX 节点的规矩，画幅由服务端按模型默认档位决定。
  if (backend === 'http') return { prompt, idempotencyKey, seconds: videoSeconds(params.seconds) }

  const size = videoSize(params.size)
  const body: InvocationBody = {
    prompt,
    idempotencyKey,
    videoFrames: videoFramesForSeconds(params.seconds),
    width: size,
    height: size
  }

  // 种子留空就是「这次随便挑一个」——不填一个 0 上去，那会变成「每次都一样」。
  const seed = params.seed.trim()
  if (seed.length > 0) {
    const parsed = Number.parseInt(seed, 10)
    if (Number.isFinite(parsed)) body.seed = parsed
  }

  return body
}
