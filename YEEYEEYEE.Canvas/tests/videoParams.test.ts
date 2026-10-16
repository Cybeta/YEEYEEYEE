import { describe, expect, it } from 'vitest'
import {
  DEFAULT_VIDEO_PARAMS,
  VIDEO_FRAMES_PER_SECOND,
  VIDEO_SIZE_OPTIONS,
  buildInvocationBody,
  videoBackendOf,
  videoEffectiveSeconds,
  videoFramesForSeconds,
  videoSeconds,
  videoSize,
  videoSizeOptions
} from '../src/shell/videoParams'

describe('文生视频请求体', () => {
  it('秒数折成节点认的帧数，边长原样带上', () => {
    const body = buildInvocationBody('web.text-to-video', '灯笼在木桌上', { seconds: '2', size: '384', seed: '' }, 'key-1')
    expect(body.videoFrames).toBe(49)
    expect(body.width).toBe(384)
    expect(body.height).toBe(384)
  })

  it('帧数只落在 1+8k 上——发 24 过去会被节点悄悄折成 17，用户要 1 秒只拿到 0.71 秒', () => {
    // 24 帧（1 秒）不是合法值：向上取到 25，宁可略长也不要短给。
    expect(videoFramesForSeconds('1')).toBe(25)
    expect(videoFramesForSeconds('2')).toBe(49)
    for (const seconds of ['1', '2', '3', '5', '10']) {
      const frames = videoFramesForSeconds(seconds)
      expect((frames - 1) % 8).toBe(0)
      expect(frames).toBeGreaterThanOrEqual(videoSeconds(seconds) * VIDEO_FRAMES_PER_SECOND)
    }
    // 折出来这一段实际多长：1 秒得到的是 1.04 秒，界面上会如实写出来。
    expect(videoEffectiveSeconds('1')).toBeCloseTo(25 / 24, 5)
  })

  it('种子留空就不带这个字段，交给服务端随机挑；填了才带上去', () => {
    const blank = buildInvocationBody('web.text-to-video', 'x', { ...DEFAULT_VIDEO_PARAMS, seed: '   ' }, 'key-2')
    expect('seed' in blank).toBe(false)

    const fixed = buildInvocationBody('web.text-to-video', 'x', { ...DEFAULT_VIDEO_PARAMS, seed: '42' }, 'key-3')
    expect(fixed.seed).toBe(42)
  })

  it('乱填的时长与边长退回默认值，不把 NaN 递给服务端', () => {
    const body = buildInvocationBody('web.text-to-video', 'x', { seconds: 'abc', size: '999', seed: '' }, 'key-4')
    expect(body.videoFrames).toBe(videoFramesForSeconds('1'))
    expect(body.width).toBe(256)
    expect(body.height).toBe(256)
  })

  it('时长有上下界：小于 1 秒按 1 秒，超过上限就夹住', () => {
    expect(videoSeconds('0')).toBe(1)
    expect(videoSeconds('-5')).toBe(1)
    expect(videoSeconds('999')).toBe(10)
  })

  it('边长只认白名单里的那几档', () => {
    expect(videoSize('256')).toBe(256)
    expect(videoSize('512')).toBe(512)
    expect(videoSize('300')).toBe(256)
  })

  it('别的技能不带视频参数——文生图收到这些字段会整次调用被拒', () => {
    const body = buildInvocationBody('comfyui.text-to-image', '一张图', { seconds: '9', size: '512', seed: '7' }, 'key-5')
    expect(body).toEqual({ prompt: '一张图', idempotencyKey: 'key-5' })
  })

  it('接口站那条视频路只发秒：帧数与种子会被自己的服务端拒收', () => {
    // 没有 ComfyUI 后端时，WebSkillJobApi 对 body 里的 videoFrames / seed / negativePrompt 一律 400
    // SKILL_INPUT_UNSUPPORTED（它不肯静默忽略），所以这里连字段都不能出现。
    const body = buildInvocationBody('web.text-to-video', '灯笼在木桌上', { seconds: '3', size: '384', seed: '7' }, 'key-6', 'http')
    expect(body).toEqual({ prompt: '灯笼在木桌上', idempotencyKey: 'key-6', seconds: 3 })
    expect('videoFrames' in body).toBe(false)
    expect('seed' in body).toBe(false)
    expect('width' in body).toBe(false)
    expect('height' in body).toBe(false)
  })

  it('接口站那条路的秒数同样有上下界，乱填退回 1 秒', () => {
    const low = buildInvocationBody('web.text-to-video', 'x', { ...DEFAULT_VIDEO_PARAMS, seconds: 'abc' }, 'key-7', 'http')
    expect(low.seconds).toBe(1)
    const high = buildInvocationBody('web.text-to-video', 'x', { ...DEFAULT_VIDEO_PARAMS, seconds: '999' }, 'key-8', 'http')
    expect(high.seconds).toBe(videoSeconds('999'))
  })

  it('边长档位只在 LTX 后端摆——接口站那条路不接受画幅，摆出来就是发不出去的档位', () => {
    expect(videoSizeOptions('comfyui')).toEqual(VIDEO_SIZE_OPTIONS)
    expect(videoSizeOptions('http')).toEqual([])
  })

  it('后端声明缺失时按 comfyui 走，不把既有形态改坏', () => {
    expect(videoBackendOf({})).toBe('comfyui')
    expect(videoBackendOf({ videoBackend: 'comfyui' })).toBe('comfyui')
    expect(videoBackendOf({ videoBackend: 'http' })).toBe('http')
  })
})
