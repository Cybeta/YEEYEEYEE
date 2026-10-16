import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import {
  attachmentPending, attachedOutputs, failMedia, hasActiveJobs, hasPendingAttachments, initialMediaLoad,
  isJobActive, jobFailureReason, jobOutputAttachmentId, jobRows, jobStateHex, jobStateLabel, jobsForNode,
  mediaFailure, nodeJobState, nodeMedia, orphanJobs, parseJobs, resolveMediaPick, retryMedia,
  type NodeJob
} from '../src/shell/nodeMedia'
import { InspectorPanel, NodeImageViewer } from '../src/shell/InspectorPanel'
import { TimelineView } from '../src/shell/views'
import { nodeStatusOf, type ChapterGroup, type ViewRecord } from '../src/shell/records'
import type { AttachmentSummary } from '../src/assets'

const media = (id: string, kind: string): AttachmentSummary => ({ id, kind, name: id, source: '' })
const job = (overrides: Partial<NodeJob> & { jobId: string; state: string }): NodeJob => overrides

describe('节点自己的产物按种类摊开', () => {
  it('图片 / 视频 / 音频 / 其他各归各位，并保留服务端给的顺序', () => {
    const split = nodeMedia([
      media('图2', 'Image'), media('视频', 'Video'), media('音', 'Audio'), media('怪东西', 'Other'), media('图1', 'Image')
    ])
    // 顺序就是服务端投影的顺序（最近在前），这里不重排——卡片取第一张当缩略图。
    expect(split.images.map((item) => item.id)).toEqual(['图2', '图1'])
    expect(split.videos.map((item) => item.id)).toEqual(['视频'])
    expect(split.audios.map((item) => item.id)).toEqual(['音'])
    expect(split.others.map((item) => item.id)).toEqual(['怪东西'])
  })

  it('没有产物时四个桶都是空的（大多数节点就是这样）', () => {
    expect(nodeMedia([])).toEqual({ images: [], videos: [], audios: [], others: [] })
  })
})

describe('任务归谁看 targetNodeId，与选中项无关', () => {
  const jobs: NodeJob[] = [
    job({ jobId: 'a1', state: 'Running', targetNodeId: 'A', progressPercent: 40 }),
    job({ jobId: 'a2', state: 'Failed', targetNodeId: 'A', errorMessage: '模型不可用' }),
    job({ jobId: 'b1', state: 'Succeeded', targetNodeId: 'B' }),
    job({ jobId: '无主', state: 'Failed' })
  ]

  it('按节点挑任务：没有目标节点的任务不归任何节点', () => {
    expect(jobsForNode(jobs, 'A').map((item) => item.jobId)).toEqual(['a1', 'a2'])
    expect(jobsForNode(jobs, 'B').map((item) => item.jobId)).toEqual(['b1'])
    expect(jobsForNode(jobs, '没这个节点')).toEqual([])
    // 空 ID 不是「全部」：那不是任何节点的任务。
    expect(jobsForNode(jobs, '')).toEqual([])
  })

  it('同一节点多个任务各占一行：在跑的和失败的不会被合成一个', () => {
    const state = nodeJobState(jobs, 'A')
    expect(state.active.map((item) => item.jobId)).toEqual(['a1'])
    expect(state.failures.map((item) => item.jobId)).toEqual(['a2'])
    expect(state.active[0].progressPercent).toBe(40)
    expect(state.failures[0].reason).toBe('模型不可用')
  })

  it('进度越界或缺失都不编：夹进 0～100', () => {
    const state = nodeJobState([
      job({ jobId: 'x', state: 'Running', targetNodeId: 'A', progressPercent: 180 }),
      job({ jobId: 'y', state: 'Queued', targetNodeId: 'A', progressPercent: -5 })
    ], 'A')
    expect(state.active.map((item) => item.progressPercent)).toEqual([100, 0])
  })

  it('任务取消后不再算「进行中」，也不算失败', () => {
    const state = nodeJobState([job({ jobId: 'c', state: 'Cancelled', targetNodeId: 'A' })], 'A')
    expect(state.active).toEqual([])
    expect(state.failures).toEqual([])
    expect(isJobActive('Cancelled')).toBe(false)
    expect(isJobActive('Running')).toBe(true)
  })

  it('整张画布上还有没有在跑的任务：只认未落终态的那些', () => {
    expect(hasActiveJobs(jobs)).toBe(true)
    expect(hasActiveJobs([job({ jobId: 'z', state: 'Succeeded', targetNodeId: 'A' })])).toBe(false)
    expect(hasActiveJobs(undefined)).toBe(false)
  })

  it('任务已结束但产物还在回挂路上时要继续盯着（pending）', () => {
    expect(hasPendingAttachments([job({ jobId: 'a', state: 'Succeeded', targetNodeId: 'A', attachmentState: 'pending' })])).toBe(true)
    // 跑着的任务不算「等回挂」：它还没有产物可挂。
    expect(hasPendingAttachments([job({ jobId: 'a', state: 'Running', targetNodeId: 'A', attachmentState: 'pending' })])).toBe(false)
    expect(hasPendingAttachments([job({ jobId: 'a', state: 'Succeeded', targetNodeId: 'A', attachmentState: 'attached' })])).toBe(false)
    expect(hasPendingAttachments([job({ jobId: 'a', state: 'Succeeded', targetNodeId: 'A', attachmentState: 'TARGET_LOCKED：节点已锁定' })])).toBe(false)
    expect(hasPendingAttachments(undefined)).toBe(false)
  })
})

describe('任务失败的原因', () => {
  it('任务本身失败时报服务端那句；没有原因也如实说', () => {
    expect(jobFailureReason(job({ jobId: 'x', state: 'Failed', errorMessage: '上游 500' }))).toBe('上游 500')
    expect(jobFailureReason(job({ jobId: 'x', state: 'Failed', errorMessage: '  ' }))).toContain('没有给出原因')
  })

  it('任务成功但产物没回挂上也算失败——否则界面会显示「成功却没有图」', () => {
    expect(jobFailureReason(job({ jobId: 'x', state: 'Succeeded', attachmentState: 'TARGET_FORBIDDEN：账号不能写入画布' })))
      .toBe('TARGET_FORBIDDEN：账号不能写入画布')
  })

  it('回挂中 / 已回挂 / 不适用都不算失败', () => {
    for (const attachmentState of ['none', 'pending', 'attached'])
      expect(jobFailureReason(job({ jobId: 'x', state: 'Succeeded', attachmentState }))).toBe('')
    expect(jobFailureReason(job({ jobId: 'x', state: 'Running' }))).toBe('')
  })
})

describe('任务状态的中文与颜色', () => {
  it('认得出的按桌面端那套词，认不出的如实回原值', () => {
    expect(jobStateLabel('Queued')).toBe('排队中')
    expect(jobStateLabel('Running')).toBe('生成中')
    expect(jobStateLabel('Succeeded')).toBe('已完成')
    expect(jobStateLabel('Failed')).toBe('失败')
    expect(jobStateLabel('Cancelled')).toBe('已取消')
    expect(jobStateLabel('  ')).toBe('未知状态')
    expect(jobStateLabel('SomethingNew')).toBe('SomethingNew')
  })

  it('颜色复用节点状态那一套，失败在哪儿都是红的', () => {
    expect(jobStateHex('Failed')).toBe(nodeStatusOf('Failed').hex)
    expect(jobStateHex('Running')).toBe(nodeStatusOf('Generating').hex)
    expect(jobStateHex('Succeeded')).toBe(nodeStatusOf('Completed').hex)
    expect(jobStateHex('某个没见过的状态')).toBe(nodeStatusOf('Draft').hex)
  })
})

describe('节点执行状态的中文（对齐桌面端 StatusText / StatusBrush）', () => {
  it('六个取值各自的词与色', () => {
    expect(nodeStatusOf('WaitingForUser')).toEqual({ label: '待补充', hex: '#DFB45A' })
    expect(nodeStatusOf('Generating')).toEqual({ label: '生成中', hex: '#4D9BFF' })
    expect(nodeStatusOf('Completed')).toEqual({ label: '已完成', hex: '#5CC99A' })
    expect(nodeStatusOf('Failed')).toEqual({ label: '失败', hex: '#E0707E' })
    expect(nodeStatusOf('NeedsReview')).toEqual({ label: '待确认', hex: '#DFB45A' })
    expect(nodeStatusOf('Draft')).toEqual({ label: '草稿', hex: '#93A1B3' })
  })

  it('认不出的状态退回中性色，不把英文枚举摆在卡片上', () => {
    expect(nodeStatusOf('BrandNew')).toEqual({ label: '草稿', hex: '#93A1B3' })
  })
})

describe('读 /api/web/jobs 的响应', () => {
  it('形状不对就抛，不拿半份数据往下画', () => {
    expect(() => parseJobs(null)).toThrow('响应格式不正确')
    expect(() => parseJobs({})).toThrow('响应格式不正确')
    expect(() => parseJobs([])).toThrow('响应格式不正确')
  })

  it('一行读不懂就跳过它，其余照收', () => {
    const parsed = parseJobs({ jobs: [
      { jobId: 'ok', state: 'Running', targetNodeId: 'A', progressPercent: 12, attachmentState: 'pending' },
      { jobId: '', state: 'Running' },
      { state: 'Running' },
      { jobId: '有 id 没状态' },
      'nonsense'
    ] })
    expect(parsed.jobs).toHaveLength(1)
    expect(parsed.jobs[0]).toMatchObject({ jobId: 'ok', state: 'Running', targetNodeId: 'A', progressPercent: 12, attachmentState: 'pending' })
    expect(parsed.invalidCount).toBe(4)
  })

  it('没有 targetNodeId 的任务收下来当成「不属于任何节点」', () => {
    const parsed = parseJobs({ jobs: [{ jobId: 'x', state: 'Succeeded', targetNodeId: '' }] })
    expect(parsed.jobs[0].targetNodeId).toBeNull()
    expect(jobsForNode(parsed.jobs, 'x'.repeat(8))).toEqual([])
  })
})

describe('主预览该显示哪一个产物', () => {
  const list = [media('新图', 'Image'), media('旧图', 'Image'), media('片子', 'Video')]

  it('默认选第一件——服务端给的顺序（最近在前），与画布卡片取第一张同一套', () => {
    expect(resolveMediaPick(list, 'node-1', null)).toBe('新图')
    expect(resolveMediaPick([], 'node-1', null)).toBe('')
  })

  it('同一节点追加产物后，选中的那一件还在就保留', () => {
    const appended = [media('更新的图', 'Image'), ...list]
    expect(resolveMediaPick(appended, 'node-1', { recordId: 'node-1', attachmentId: '旧图' })).toBe('旧图')
  })

  it('选中的那一件被移除后回到剩下的第一件', () => {
    const rest = [media('新图', 'Image'), media('片子', 'Video')]
    expect(resolveMediaPick(rest, 'node-1', { recordId: 'node-1', attachmentId: '旧图' })).toBe('新图')
  })

  it('换节点不沿用上一个节点的选择：一切从头算，不残留旧媒体的选择', () => {
    expect(resolveMediaPick(list, 'node-2', { recordId: 'node-1', attachmentId: '旧图' })).toBe('新图')
  })
})

describe('产物加载失败与重试', () => {
  it('没失败时原因是空串（重试过也不算）', () => {
    expect(mediaFailure(initialMediaLoad)).toBe('')
    expect(mediaFailure(retryMedia(initialMediaLoad))).toBe('')
  })

  it('失败只记在它自己那一次尝试上', () => {
    const failed = failMedia(initialMediaLoad, 0, '图片读不到')
    expect(mediaFailure(failed)).toBe('图片读不到')
    expect(failed.attempt).toBe(0)
  })

  it('重试换一个尝试序号并收起提示——地址跟着变，才会真的重新取一次', () => {
    const retried = retryMedia(failMedia(initialMediaLoad, 0, '图片读不到'))
    expect(retried.attempt).toBe(1)
    expect(mediaFailure(retried)).toBe('')
  })

  it('上一件 / 上一次尝试的迟到事件落不到当前状态上', () => {
    const retried = retryMedia(failMedia(initialMediaLoad, 0, '图片读不到'))
    const late = failMedia(retried, 0, '上一张的迟到失败')
    expect(late).toBe(retried)
    expect(mediaFailure(late)).toBe('')
    // 反过来也一样：还没轮到的那一次尝试说了不算。
    expect(mediaFailure(failMedia(retried, 2, '未来的失败'))).toBe('')
  })
})

/**
 * 检查器里「节点自身媒体」那一块的实际输出。
 *
 * 这里渲染的是**真的组件**（不是另写一份结构）：主预览放哪一件、切换入口长什么样、
 * 音频与其它产物还在不在，都在下面这些断言里钉住。SSR 不跑 effect，所以不会发请求。
 */
describe('检查器的节点自身媒体', () => {
  const NODE = '00000000-0000-4000-8000-000000000001'
  const own = (id: string, kind: string, name = id): AttachmentSummary => ({ id, kind, name, source: 'web-chat' })
  const record = (attachments: AttachmentSummary[]): ViewRecord => ({
    recordId: NODE,
    recordType: 'storyboard',
    record: { title: '镜头', content: '正文', x: 0, y: 0, attachments }
  })
  const panel = (attachments: AttachmentSummary[]) => renderToStaticMarkup(createElement(InspectorPanel, {
    selected: record(attachments), records: [], title: '镜头', content: '正文', dirty: false, saving: false,
    readOnly: false, canEdit: true, hint: '', assets: [], assetsReady: true,
    onTitle: () => {}, onContent: () => {}, onApply: () => {}
  }))
  const stageImage = (markup: string) => /class="df-media-stage-image" src="([^"]+)"/.exec(markup)?.[1] ?? ''

  it('没有产物时整块都不出现', () => {
    const markup = panel([])
    expect(markup).not.toContain('产物附件')
    expect(markup).not.toContain('df-media-stage')
  })

  it('单张图：主预览就是它，点它看大图', () => {
    const markup = panel([own('a1', 'Image', '新图')])
    expect(markup).toContain('主预览')
    expect(markup).toContain('图片 · 新图')
    expect(stageImage(markup)).toBe(`/api/web/records/${NODE}/attachments/a1/file`)
    expect(markup).toContain('df-media-stage is-openable')
    expect(markup).toContain('点击查看大图')
    expect(markup).toContain('当前主预览')
  })

  it('多张图：主预览是最新那一张，列表里它标成当前、别的可点着换', () => {
    const markup = panel([own('a1', 'Image', '新图'), own('a2', 'Image', '旧图')])
    expect(stageImage(markup)).toBe(`/api/web/records/${NODE}/attachments/a1/file`)
    expect(markup).toContain('aria-pressed="true" aria-label="当前主预览：新图"')
    expect(markup).toContain('aria-pressed="false" aria-label="设为主预览：旧图"')
    // 两张图的缩略图都在（选择入口），名称与下载入口也都在。
    expect(markup).toContain('下载 新图')
    expect(markup).toContain('下载 旧图')
  })

  it('只有视频：主预览是原生播放器，controls + metadata 预加载、不自动播放', () => {
    const markup = panel([own('v1', 'Video', '片子')])
    expect(markup).toContain('class="df-media-stage-video"')
    expect(markup).toContain('controls')
    expect(markup).toContain('preload="metadata"')
    expect(markup).not.toContain('autoplay')
    expect(markup).toContain(`/api/web/records/${NODE}/attachments/v1/file`)
  })

  it('图片与视频混合：图片先排（与画布卡片同一套），视频在那排里是一枚「▶」入口', () => {
    const markup = panel([own('v1', 'Video', '片子'), own('a1', 'Image', '新图')])
    expect(stageImage(markup)).toBe(`/api/web/records/${NODE}/attachments/a1/file`)
    expect(markup).toContain('aria-label="设为主预览：片子"')
    // 视频没被选中时**一个播放器都不摆**：列表里只有「▶」，同一件产物不会在两个位置同时播 / 重复抓元数据。
    expect(markup.match(/<video/g) ?? []).toEqual([])
  })

  it('音频保留行内播放器与下载入口，不因主预览改造丢掉', () => {
    const markup = panel([own('s1', 'Audio', '配乐')])
    expect(markup).not.toContain('df-media-stage')
    expect(markup).toContain('<audio')
    expect(markup).toContain('preload="metadata"')
    expect(markup).toContain(`/api/web/records/${NODE}/attachments/s1/file`)
    expect(markup).toContain('下载 配乐')
  })

  it('认不出种类的产物照样列出它的种类与下载入口', () => {
    const markup = panel([own('o1', 'Other', '打包件')])
    expect(markup).toContain('其他')
    expect(markup).toContain('下载 打包件')
  })

  it('大图：多张时能看到当前第几张，并给上一张 / 下一张与关闭', () => {
    const images = [own('a1', 'Image', '新图'), own('a2', 'Image', '旧图')]
    const markup = renderToStaticMarkup(createElement(NodeImageViewer, {
      recordId: NODE, images, index: 1, onIndex: () => {}, onClose: () => {}
    }))
    expect(markup).toContain('role="dialog"')
    // is-multi：多图时大图要让出高度给缩略带，否则那条带子会被浮层的 flex 压扁。
    expect(markup).toContain('class="df-ref-viewer is-multi"')
    expect(markup).toContain('第 2 / 2 张')
    expect(markup).toContain('aria-current="true" aria-label="第 2 张：旧图"')
    expect(markup).toContain(`/api/web/records/${NODE}/attachments/a2/file`)
    expect(markup).toContain('上一张')
    expect(markup).toContain('关闭（Esc）')
    expect(markup).not.toContain('disabled=""')
  })

  it('大图只有一张时不给切换按钮，也不摆缩略带', () => {
    const markup = renderToStaticMarkup(createElement(NodeImageViewer, {
      recordId: NODE, images: [own('a1', 'Image', '新图')], index: 0, onIndex: () => {}, onClose: () => {}
    }))
    expect(markup).toContain('第 1 / 1 张')
    expect(markup).not.toContain('df-media-viewer-strip')
    expect(markup).not.toContain('is-multi')
    expect(markup.match(/disabled/g) ?? []).toHaveLength(2)
  })
})

/**
 * 服务端给产物算的那个附件 ID。
 *
 * 期望值来自**服务端那段代码的实际输出**（`ProjectCanvasSceneStore.AttachJobOutputs`：
 * `new Guid(SHA256.HashData("web-job:{jobId}:{index}")[..16])`，用同一份 .NET 环境跑出来的），
 * 不是这里先算一遍再抄回来——否则字节序写反了测试照样绿，而界面上永远看不到产物。
 */
describe('任务产物的附件 ID 必须与服务端算出来的那一个逐字相同', () => {
  it('SHA256("web-job:{id}:{index}") 的前 16 字节 + .NET Guid 的字节序', () => {
    expect(jobOutputAttachmentId('3f2504e0-4f89-11d3-9a0c-0305e82c3301', 0))
      .toBe('70981bec-5398-3f23-c710-1e459585695e')
    expect(jobOutputAttachmentId('3f2504e0-4f89-11d3-9a0c-0305e82c3301', 1))
      .toBe('6044ac1e-9bdc-3041-6753-2cd1f6ebb1ea')
    expect(jobOutputAttachmentId('3f2504e0-4f89-11d3-9a0c-0305e82c3301', 2))
      .toBe('ac15cc5c-0c58-8fed-6e87-39ffdc53e5f3')
    expect(jobOutputAttachmentId('00000000-0000-4000-8000-000000000001', 0))
      .toBe('50e60df6-e16e-69bb-5971-55e070c75e90')
  })

  it('同一任务的不同下标、不同任务的同一下标都不会算成同一个——否则产物会互相冒充', () => {
    const ids = new Set([
      jobOutputAttachmentId('a', 0), jobOutputAttachmentId('a', 1),
      jobOutputAttachmentId('b', 0), jobOutputAttachmentId('b', 1)
    ])
    expect(ids.size).toBe(4)
    for (const id of ids) expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/)
  })
})

describe('一件产物算不算这个任务的', () => {
  const jobId = '3f2504e0-4f89-11d3-9a0c-0305e82c3301'
  const derived = jobOutputAttachmentId(jobId, 0)
  const outputs = [{ role: 'image', ref: 'asset://a.png' }]
  const own = (id: string, kind = 'Image', name = '图'): AttachmentSummary => ({ id, kind, name, source: 'web.text-to-image' })

  it('任务成功 + 服务端说已回挂 + 附件身份对得上：才算它的', () => {
    const job: NodeJob = { jobId, state: 'Succeeded', attachmentState: 'attached', outputs }
    expect(attachedOutputs(job, [own(derived, 'Image', '图片 · 3f2504e0 · 1')]))
      .toEqual([{ attachmentId: derived, kind: 'Image', name: '图片 · 3f2504e0 · 1' }])
    // ID 大小写不同也要认（服务端给的是小写，但别把大小写当成身份差异）。
    expect(attachedOutputs(job, [own(derived.toUpperCase())])).toHaveLength(1)
  })

  it('任务还在跑 / 产物还在回挂 / 任务失败：都不显示', () => {
    expect(attachedOutputs({ jobId, state: 'Running', attachmentState: 'attached', outputs }, [own(derived)])).toEqual([])
    expect(attachedOutputs({ jobId, state: 'Succeeded', attachmentState: 'pending', outputs }, [own(derived)])).toEqual([])
    expect(attachedOutputs({ jobId, state: 'Failed', attachmentState: 'attached', outputs }, [own(derived)])).toEqual([])
    expect(attachedOutputs({ jobId, state: 'Cancelled', attachmentState: 'attached', outputs }, [own(derived)])).toEqual([])
  })

  it('身份对不上就空着：不认「任务成功了就把节点上的图都算它的」', () => {
    const job: NodeJob = { jobId, state: 'Succeeded', attachmentState: 'attached', outputs }
    expect(attachedOutputs(job, [own('别人任务的图')])).toEqual([])
    expect(attachedOutputs(job, [])).toEqual([])
    // 没有 outputs 就说不出「它出了几件」，也就没有可认的东西。
    expect(attachedOutputs({ jobId, state: 'Succeeded', attachmentState: 'attached' }, [own(derived)])).toEqual([])
  })

  it('多件产物按 index 逐个对位，顺序不重排', () => {
    const job: NodeJob = {
      jobId, state: 'Succeeded', attachmentState: 'attached',
      outputs: [{ role: 'image', ref: 'a' }, { role: 'image', ref: 'b' }]
    }
    const list = [own(jobOutputAttachmentId(jobId, 1), 'Video', '第二件'), own(jobOutputAttachmentId(jobId, 0), 'Image', '第一件')]
    expect(attachedOutputs(job, list).map((item) => item.name)).toEqual(['第一件', '第二件'])
  })
})

describe('时间轴上的任务行', () => {
  const outputs = [{ role: 'image', ref: 'asset://a.png' }]

  it('一行一个任务：在跑的、失败的、取消的各说各的，谁也不盖谁', () => {
    const rows = jobRows([
      job({ jobId: 'a', state: 'Running', targetNodeId: 'N', progressPercent: 30 }),
      job({ jobId: 'b', state: 'Failed', targetNodeId: 'N', errorMessage: '上游 500' }),
      job({ jobId: 'c', state: 'Cancelled', targetNodeId: 'N' }),
      job({ jobId: 'd', state: 'Running', targetNodeId: '别的节点' })
    ], 'N', [])
    expect(rows.map((row) => [row.jobId, row.label, row.active, row.progress, row.failure]))
      .toEqual([
        ['a', '生成中', true, 30, ''],
        ['b', '失败', false, null, '上游 500'],
        ['c', '已取消', false, null, '']
      ])
  })

  it('进度：越界的夹进 0～100；缺值 / 非有限值一律是 null（不编 0%）', () => {
    const rows = jobRows([
      job({ jobId: 'a', state: 'Running', targetNodeId: 'N', progressPercent: 180 }),
      job({ jobId: 'b', state: 'Queued', targetNodeId: 'N', progressPercent: -5 }),
      job({ jobId: 'c', state: 'Running', targetNodeId: 'N' }),
      job({ jobId: 'd', state: 'Running', targetNodeId: 'N', progressPercent: Number.NaN }),
      job({ jobId: 'e', state: 'Running', targetNodeId: 'N', progressPercent: Number.POSITIVE_INFINITY }),
      // 已经落终态的任务没有进度可谈——它那个数字是上一刻的残留。
      job({ jobId: 'f', state: 'Succeeded', targetNodeId: 'N', progressPercent: 42, attachmentState: 'none' })
    ], 'N', [])
    expect(rows.map((row) => row.progress)).toEqual([100, 0, null, null, null, null])
  })

  it('任务成功、产物还在回挂路上：pending 单独说清楚', () => {
    const rows = jobRows([
      job({ jobId: 'a', state: 'Succeeded', targetNodeId: 'N', attachmentState: 'pending', outputs })
    ], 'N', [])
    expect(rows[0].pending).toBe(true)
    expect(rows[0].failure).toBe('')
    expect(rows[0].outputs).toEqual([])
    expect(attachmentPending({ jobId: 'a', state: 'Running', attachmentState: 'pending' })).toBe(false)
  })

  it('回挂失败是失败：任务显示成功也要把服务端那句原因带出来', () => {
    const rows = jobRows([
      job({ jobId: 'a', state: 'Succeeded', targetNodeId: 'N', attachmentState: 'TARGET_LOCKED：任务目标节点已锁定', outputs })
    ], 'N', [])
    expect(rows[0].failure).toBe('TARGET_LOCKED：任务目标节点已锁定')
    expect(rows[0].outputs).toEqual([])
  })
})

describe('目标节点已经不在画布上的任务', () => {
  it('只按**全部**节点判断：被筛掉的节点不是被删的', () => {
    const jobs: NodeJob[] = [
      job({ jobId: 'a', state: 'Running', targetNodeId: 'A' }),
      job({ jobId: 'b', state: 'Running', targetNodeId: 'B' }),
      job({ jobId: '无主', state: 'Running' })
    ]
    // A 在画布上、B 已经没了、没有 targetNodeId 的不算「目标不可用」（它本来就不属于任何节点）。
    expect(orphanJobs(jobs, ['A']).map((item) => item.jobId)).toEqual(['b'])
    expect(orphanJobs(jobs, ['A', 'B'])).toEqual([])
    expect(orphanJobs(undefined, ['A'])).toEqual([])
  })
})

describe('读 /api/web/jobs 里的 outputs', () => {
  it('按原顺序读下来；读不懂的那一行**留着位置**（下标就是产物身份，不能前移）', () => {
    const parsed = parseJobs({ jobs: [{ jobId: 'j', state: 'Succeeded', outputs: [
      { role: 'image', ref: 'asset://a.png' }, 'garbage', { role: 7 }, null
    ] }] })
    expect(parsed.jobs[0].outputs).toEqual([
      { role: 'image', ref: 'asset://a.png' },
      { role: '', ref: '' },
      { role: '', ref: '' },
      { role: '', ref: '' }
    ])
  })

  it('没有 outputs 就是空的，不是 undefined', () => {
    expect(parseJobs({ jobs: [{ jobId: 'j', state: 'Running' }] }).jobs[0].outputs).toEqual([])
  })
})

/**
 * 时间轴的实际输出。
 *
 * 渲染的是**真的组件**（不是另写一份结构）：任务归哪个节点、进度怎么显示、产物什么时候才出现，
 * 都在下面这些断言里钉住。SSR 不跑 effect，所以不会发请求。
 */
describe('时间轴：任务进度、终态与产物', () => {
  const NODE_A = '11111111-1111-4111-8111-111111111111'
  const NODE_B = '22222222-2222-4222-8222-222222222222'
  const NODE_GONE = '99999999-9999-4999-8999-999999999999'
  const JOB_A = '3f2504e0-4f89-11d3-9a0c-0305e82c3301'
  const JOB_B = 'ffffffff-ffff-ffff-ffff-ffffffffffff'

  const nodeRecord = (recordId: string, title: string, attachments: AttachmentSummary[] = []): ViewRecord => ({
    recordId, recordType: 'storyboard', record: { title, content: '正文', x: 0, y: 0, attachments }
  })
  const lane = (records: ViewRecord[]): ChapterGroup[] =>
    [{ id: 'chapter-1', label: '第一章', order: 1, body: '', records }]

  function timeline(options: {
    records?: ViewRecord[]
    jobs?: NodeJob[]
    nodeIds?: string[]
    selectedId?: string
  } = {}): string {
    return renderToStaticMarkup(createElement(TimelineView, {
      groups: lane(options.records ?? [nodeRecord(NODE_A, '镜头一')]),
      activeChapter: 'all',
      selectedId: options.selectedId ?? '',
      jobs: options.jobs,
      nodeIds: options.nodeIds ?? [NODE_A, NODE_B],
      onChapter: () => {},
      onSelect: () => {}
    }))
  }
  const rowTitles = (markup: string) => [...markup.matchAll(/title="任务 ([^ ]+) /g)].map((match) => match[1])

  it('单个任务：排队 → 生成中（带进度）→ 已完成，进度只在还在跑的时候出现', () => {
    const queued = timeline({ jobs: [job({ jobId: JOB_A, state: 'Queued', targetNodeId: NODE_A })] })
    expect(queued).toContain('排队中')

    const running = timeline({ jobs: [job({ jobId: JOB_A, state: 'Running', targetNodeId: NODE_A, progressPercent: 42.6 })] })
    expect(running).toContain('生成中')
    expect(running).toContain('43%')
    expect(running).toContain('aria-valuenow="43"')

    const done = timeline({ jobs: [job({ jobId: JOB_A, state: 'Succeeded', targetNodeId: NODE_A, attachmentState: 'none' })] })
    expect(done).toContain('已完成')
    expect(done).not.toContain('aria-valuenow')
    expect(done).not.toContain('%')
  })

  it('拿不到有效进度就说「处理中」，不编一个百分比出来', () => {
    const markup = timeline({ jobs: [job({ jobId: JOB_A, state: 'Running', targetNodeId: NODE_A })] })
    expect(markup).toContain('处理中…')
    expect(markup).not.toContain('aria-valuenow')

    const clamped = timeline({ jobs: [job({ jobId: JOB_A, state: 'Running', targetNodeId: NODE_A, progressPercent: 380 })] })
    expect(clamped).toContain('100%')
    expect(clamped).not.toContain('380')
  })

  it('同一节点上并行两个任务：各占一行，一个在跑一个失败都看得见', () => {
    const markup = timeline({ jobs: [
      job({ jobId: JOB_A, state: 'Running', targetNodeId: NODE_A, progressPercent: 10 }),
      job({ jobId: JOB_B, state: 'Failed', targetNodeId: NODE_A, errorMessage: '上游 500' })
    ] })
    expect(markup.match(/class="df-lane-task"/g)).toHaveLength(2)
    expect(markup).toContain('生成中')
    expect(markup).toContain('失败')
    expect(markup).toContain('上游 500')
  })

  it('任务只归它自己的 targetNodeId：换选中项也不会跑到别的节点上', () => {
    const jobs = [job({ jobId: JOB_B, state: 'Running', targetNodeId: NODE_B })]
    const onOtherNode = timeline({ records: [nodeRecord(NODE_A, '镜头一')], jobs, selectedId: NODE_A })
    expect(onOtherNode).not.toContain('df-lane-task')
    // 选中项换成别的节点，任务仍在它自己的目标节点上——顺序也不跟着变。
    const onItsNode = timeline({ records: [nodeRecord(NODE_B, '镜头二')], jobs, selectedId: NODE_A })
    expect(rowTitles(onItsNode)).toEqual([JOB_B])
  })

  it('任务行的顺序只跟服务端给的顺序走', () => {
    const jobs = [
      job({ jobId: JOB_A, state: 'Running', targetNodeId: NODE_A }),
      job({ jobId: JOB_B, state: 'Queued', targetNodeId: NODE_A })
    ]
    expect(rowTitles(timeline({ jobs, selectedId: NODE_A }))).toEqual([JOB_A, JOB_B])
    expect(rowTitles(timeline({ jobs, selectedId: '' }))).toEqual([JOB_A, JOB_B])
  })

  it('succeeded + pending：先如实说产物还在回挂，回挂完成（attached）之后才出现媒体', () => {
    const attachmentId = jobOutputAttachmentId(JOB_A, 0)
    const outputs = [{ role: 'image', ref: 'asset://a.png' }]
    const attachments: AttachmentSummary[] = [
      { id: attachmentId, kind: 'Image', name: '图片 · 3f2504e0 · 1', source: 'web.text-to-image' }
    ]
    const base = { jobId: JOB_A, state: 'Succeeded', targetNodeId: NODE_A, outputs }

    const pending = timeline({ records: [nodeRecord(NODE_A, '镜头一', attachments)],
      jobs: [job({ ...base, attachmentState: 'pending' })] })
    expect(pending).toContain('已完成')
    expect(pending).toContain('产物还在回挂中…')
    expect(pending).not.toContain('<img')

    const attached = timeline({ records: [nodeRecord(NODE_A, '镜头一', attachments)],
      jobs: [job({ ...base, attachmentState: 'attached' })] })
    expect(attached).not.toContain('产物还在回挂中')
    // 取图走的是按 ID 的安全接口，不是任务输出里的路径。
    expect(attached).toContain(`/api/web/records/${NODE_A}/attachments/${attachmentId}/file`)
    expect(attached).not.toContain('asset://')
  })

  it('任务成功但产物没回挂上：显示服务端那句原因，不显示任何媒体', () => {
    const markup = timeline({
      records: [nodeRecord(NODE_A, '镜头一', [
        { id: jobOutputAttachmentId(JOB_A, 0), kind: 'Image', name: '本来该挂上来的图', source: '' }
      ])],
      jobs: [job({
        jobId: JOB_A, state: 'Succeeded', targetNodeId: NODE_A,
        attachmentState: 'TARGET_LOCKED：任务目标节点已锁定',
        outputs: [{ role: 'image', ref: 'asset://a.png' }]
      })]
    })
    expect(markup).toContain('TARGET_LOCKED：任务目标节点已锁定')
    expect(markup).not.toContain('<img')
    expect(markup).not.toContain('本来该挂上来的图')
    expect(markup).not.toContain('产物还在回挂中')
  })

  it('失败与取消是两回事；两者都不改节点自己的状态', () => {
    const failed = timeline({ jobs: [job({ jobId: JOB_A, state: 'Failed', targetNodeId: NODE_A, errorMessage: '上游 500' })] })
    expect(failed).toContain('上游 500')
    expect(failed).toContain('df-lane-task-fail')
    // 节点自己那一行状态仍来自记录（草稿），任务失败没有被写成节点的错误。
    expect(failed).toContain('草稿')

    const cancelled = timeline({ jobs: [job({ jobId: JOB_A, state: 'Cancelled', targetNodeId: NODE_A })] })
    expect(cancelled).toContain('已取消')
    expect(cancelled).not.toContain('df-lane-task-fail')
  })

  it('同一节点前后两个任务：产物按 ID 各自认领，图片给缩略图、视频给「种类 · 名称」', () => {
    const attachments: AttachmentSummary[] = [
      { id: jobOutputAttachmentId(JOB_A, 0), kind: 'Image', name: '第一张', source: '' },
      { id: jobOutputAttachmentId(JOB_B, 0), kind: 'Video', name: '第二段', source: '' }
    ]
    const markup = timeline({
      records: [nodeRecord(NODE_A, '镜头一', attachments)],
      jobs: [
        job({ jobId: JOB_A, state: 'Succeeded', targetNodeId: NODE_A, attachmentState: 'attached', outputs: [{ role: 'image', ref: 'a' }] }),
        job({ jobId: JOB_B, state: 'Succeeded', targetNodeId: NODE_A, attachmentState: 'attached', outputs: [{ role: 'video', ref: 'b' }] })
      ]
    })
    expect(markup).toContain(`/api/web/records/${NODE_A}/attachments/${jobOutputAttachmentId(JOB_A, 0)}/file`)
    expect(markup).toContain('图片 · 第一张')
    expect(markup).toContain('alt="第一张"')
    expect(markup).toContain('视频 · 第二段')
    // 视频不摆播放器：那是节点主预览的活，摆在时间轴上会同时抓元数据、还占掉泳道高度。
    expect(markup.match(/<img/g)).toHaveLength(1)
    expect(markup).not.toContain('<video')
  })

  it('服务端说已回挂、但附件身份对不上：什么都不显示（宁可空着也不认错）', () => {
    const markup = timeline({
      records: [nodeRecord(NODE_A, '镜头一', [{ id: '别人任务的图', kind: 'Image', name: '别人任务的图', source: '' }])],
      jobs: [job({ jobId: JOB_A, state: 'Succeeded', targetNodeId: NODE_A, attachmentState: 'attached', outputs: [{ role: 'image', ref: 'a' }] })]
    })
    expect(markup).not.toContain('<img')
    expect(markup).not.toContain('别人任务的图')
  })

  it('目标节点已删除：另起一条说清「不可用」，不归到当前选中的节点上', () => {
    const markup = timeline({
      records: [nodeRecord(NODE_A, '镜头一')],
      nodeIds: [NODE_A, NODE_B],
      selectedId: NODE_A,
      jobs: [job({
        jobId: JOB_A, state: 'Succeeded', targetNodeId: NODE_GONE,
        attachmentState: 'TARGET_NOT_FOUND：任务目标节点已删除',
        outputs: [{ role: 'image', ref: 'asset://a.png' }]
      })]
    })
    expect(markup).toContain('任务目标不可用')
    expect(markup).toContain(NODE_GONE)
    expect(markup).toContain('目标节点已删除或不可用')
    expect(markup).toContain('TARGET_NOT_FOUND：任务目标节点已删除')
    // 没有任何一张节点卡认领它。
    expect(markup).not.toContain('class="df-lane-task"')
  })

  it('只是被阶段/搜索筛掉的节点不算被删：不报「目标不可用」', () => {
    const markup = timeline({
      records: [nodeRecord(NODE_A, '镜头一')],
      nodeIds: [NODE_A, NODE_B],
      jobs: [job({ jobId: JOB_A, state: 'Running', targetNodeId: NODE_B })]
    })
    expect(markup).not.toContain('任务目标不可用')
  })

  it('点任务 / 产物定位到的是它自己的目标节点：链接文本就是那个节点', () => {
    const attachments: AttachmentSummary[] = [
      { id: jobOutputAttachmentId(JOB_A, 0), kind: 'Image', name: '第一张', source: '' }
    ]
    const markup = timeline({
      records: [nodeRecord(NODE_A, '镜头一', attachments)],
      jobs: [job({ jobId: JOB_A, state: 'Succeeded', targetNodeId: NODE_A, attachmentState: 'attached', outputs: [{ role: 'image', ref: 'a' }] })]
    })
    // 任务行与产物都在渲染这个节点的那张卡里（卡片是按 recordId 逐张渲染的，
    // 而卡里的 recordId 就是任务的 targetNodeId——见 jobsForNode）。
    expect(markup).toContain('镜头一')
    expect(markup).toContain(`/api/web/records/${NODE_A}/attachments/`)
    expect(rowTitles(markup)).toEqual([JOB_A])
  })
})
