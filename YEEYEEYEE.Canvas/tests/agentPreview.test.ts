import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { AttachmentSummary, Reference } from '../src/assets'
import { ALL_CHAPTERS_ID } from '../src/ChapterView'
import {
  agentPreviewAssetDiffs, agentPreviewLayer, attachmentDeltas, describeNodeAssetDelta, parseAgentPreview,
  previewAttachmentFileUrl, referenceDeltas
} from '../src/shell/agentPreview'
import { PreviewAssetChanges } from '../src/shell/ChatPanel'
import type { ViewRecord } from '../src/shell/records'

const NODE_A = '00000000-0000-4000-8000-000000000001'
const NODE_B = '00000000-0000-4000-8000-000000000002'
const NODE_C = '00000000-0000-4000-8000-000000000003'
const NODE_D = '00000000-0000-4000-8000-000000000004'

const attachment = (id: string, name = '图', kind = 'Image', source = 'web-chat'): AttachmentSummary =>
  ({ id, name, kind, source })

const reference = (
  entityId: string,
  variantId: string | undefined,
  variantVersionId: string | null = null,
  name = '沈砚',
  variantLabel = '少年黑衣',
  versions: Reference['versions'] = []
): Reference => ({ entityId, name, kind: 'Character', variantId, variantVersionId, variantLabel, versions })

const record = (recordId: string, title: string, x: number, extra: Record<string, unknown> = {}): ViewRecord => ({
  recordId,
  recordType: 'storyboard',
  record: { title, content: 'content', x, y: 20, ...extra }
})

const preview = (diff: Partial<{
  addedNodes: ViewRecord[]
  updatedNodes: ViewRecord[]
  removedNodes: ViewRecord[]
  addedEdges: { edgeId: string; sourceId: string; targetId: string }[]
  removedEdges: { edgeId: string; sourceId: string; targetId: string }[]
}>) => parseAgentPreview({
  sessionId: 'session', messageId: 'message', revision: 7,
  diff: { addedNodes: [], updatedNodes: [], removedNodes: [], addedEdges: [], removedEdges: [], ...diff }
})

describe('Agent canvas preview', () => {
  it('projects server node and edge diffs without mutating formal records', () => {
    const original = record(NODE_A, 'Original', 10)
    const added = record(NODE_B, 'Added', 200)
    const updated = record(NODE_A, 'Updated', 100)
    const removed = record(NODE_C, 'Removed', 300)
    const records = [original, removed]
    const before = structuredClone(records)
    const parsed = preview({
      addedNodes: [added], updatedNodes: [updated], removedNodes: [removed],
      addedEdges: [{ edgeId: 'edge-added', sourceId: updated.recordId, targetId: added.recordId }],
      removedEdges: [{ edgeId: 'edge-removed', sourceId: original.recordId, targetId: removed.recordId }]
    })

    const layer = agentPreviewLayer(parsed, records, ALL_CHAPTERS_ID)
    expect(layer.nodes.map(item => [item.change, item.node.recordId])).toEqual([
      ['added', added.recordId], ['updated', updated.recordId], ['removed', removed.recordId]
    ])
    expect(layer.nodes.every(item => describeNodeAssetDelta(item.assets) === '')).toBe(true)
    expect(layer.edges.map(item => [item.change, item.edge.edgeId, item.from.recordId, item.to.recordId])).toEqual([
      ['added', 'edge-added', updated.recordId, added.recordId],
      ['removed', 'edge-removed', original.recordId, removed.recordId]
    ])
    expect(records).toEqual(before)
    expect(original.record.title).toBe('Original')
  })

  it('rejects malformed preview identity and revision', () => {
    const raw = {
      sessionId: 'session', messageId: 'message', revision: 7,
      diff: { addedNodes: [], updatedNodes: [], removedNodes: [], addedEdges: [], removedEdges: [] }
    }
    expect(() => parseAgentPreview({ ...raw, sessionId: '' })).toThrow('预览格式')
    expect(() => parseAgentPreview({ ...raw, revision: -1 })).toThrow('预览格式')
  })
})

describe('Agent approval attachment and reference deltas', () => {
  it('splits attachments by product ID into added, updated and removed', () => {
    const kept = attachment('a1', '甲')
    const renamed = attachment('a2', '乙')
    const gone = attachment('a3', '丙')
    const fresh = attachment('a4', '丁', 'Video')
    const deltas = attachmentDeltas([kept, renamed, gone], [kept, { ...renamed, name: '乙（改名）' }, fresh])

    expect(deltas.map(item => [item.change, item.id])).toEqual([
      ['updated', 'a2'], ['added', 'a4'], ['removed', 'a3']
    ])
    expect(deltas[0].fields).toEqual([{ label: '名称', before: '乙', after: '乙（改名）' }])
    expect(deltas[1].before).toBeNull()
    expect(deltas[2].after).toBeNull()
  })

  it('never matches attachments that share a name but not an ID', () => {
    const deltas = attachmentDeltas([attachment('a1', '同名')], [attachment('a2', '同名')])
    expect(deltas.map(item => item.change)).toEqual(['added', 'removed'])
  })

  it('treats follow-latest and locked-version as one modified reference', () => {
    const deltas = referenceDeltas(
      [reference('e1', 'v1')],
      [reference('e1', 'v1', 'ver2', '沈砚', '少年黑衣', [{ id: 'ver2', number: 2, label: 'v2' }])]
    )
    expect(deltas).toHaveLength(1)
    expect(deltas[0].change).toBe('updated')
    expect(deltas[0].key).toBe('e1|v1|ver2')
    expect(deltas[0].fields).toEqual([{ label: '版本', before: '跟随最新', after: '锁定版本 v2' }])
  })

  it('keeps switching a variant as one addition plus one removal, by ID', () => {
    const first = reference('e1', 'v1')
    const second = reference('e1', 'v2')
    expect(referenceDeltas([first], [second]).map(item => [item.change, item.key])).toEqual([
      ['added', 'e1|v2|latest'], ['removed', 'e1|v1|latest']
    ])
    const namesake = reference('e9', 'v1')
    expect(referenceDeltas([first], [namesake]).map(item => item.change)).toEqual(['added', 'removed'])
  })

  it('reports unchanged references and attachments as no change at all', () => {
    const same = reference('e1', 'v1', 'ver1', '沈砚', '少年黑衣', [{ id: 'ver1', number: 1, label: 'v1' }])
    expect(referenceDeltas([same], [{ ...same }])).toEqual([])
    expect(attachmentDeltas([attachment('a1', '甲')], [attachment('a1', '甲')])).toEqual([])
  })

  it('shows added and removed node media and references without touching formal records', () => {
    const before = record(NODE_A, '原有', 10, {
      attachments: [attachment('a1', '保留'), attachment('a2', '改前')],
      references: [reference('e1', 'v1')]
    })
    const after = record(NODE_A, '原有', 10, {
      attachments: [attachment('a1', '保留'), attachment('a2', '改后'), attachment('a3', '新增')],
      references: [reference('e1', 'v1'), reference('e2', 'v1', null, '林夏')]
    })
    const added = record(NODE_B, '新节点', 200, {
      attachments: [attachment('b1', '新图')],
      references: [reference('e3', 'v1', null, '黄铜钥匙')]
    })
    const removed = record(NODE_C, '待删节点', 300, {
      attachments: [attachment('c1', '旧图', 'Video')],
      references: [reference('e4', 'v1', null, '旧书店')]
    })
    const records = [before, removed]
    const snapshot = structuredClone(records)
    const parsed = preview({ addedNodes: [added], updatedNodes: [after], removedNodes: [removed] })

    const diffs = agentPreviewAssetDiffs(parsed, records)
    expect(diffs.map(item => [item.recordId, item.change])).toEqual([
      [NODE_B, 'added'], [NODE_A, 'updated'], [NODE_C, 'removed']
    ])
    expect(describeNodeAssetDelta(diffs[0])).toBe('附件 ＋1 · 引用 ＋1')
    expect(describeNodeAssetDelta(diffs[1])).toBe('附件 ＋1 △1 · 引用 ＋1')
    expect(describeNodeAssetDelta(diffs[2])).toBe('附件 −1 · 引用 −1')
    expect(diffs[0].attachments.map(item => [item.change, item.after?.name])).toEqual([['added', '新图']])
    expect(diffs[1].attachments.map(item => [item.change, item.id])).toEqual([['updated', 'a2'], ['added', 'a3']])
    expect(diffs[1].references.map(item => [item.change, item.after?.name])).toEqual([['added', '林夏']])
    expect(diffs[2].attachments.map(item => [item.change, item.before?.name, item.after])).toEqual([['removed', '旧图', null]])
    expect(diffs[2].references.map(item => [item.change, item.before?.name, item.after])).toEqual([['removed', '旧书店', null]])
    // 预览只读：正式记录（含它们身上的附件与引用）一个字都没被改写。
    expect(records).toEqual(snapshot)
  })

  it('ignores nodes whose attachments and references did not change', () => {
    const shared = { attachments: [attachment('a1', '甲')], references: [reference('e1', 'v1')] }
    const diffs = agentPreviewAssetDiffs(
      preview({ updatedNodes: [record(NODE_D, '只改了文字', 10, { ...shared })] }),
      [record(NODE_D, '只改了文字', 10, { ...shared })]
    )
    expect(diffs).toEqual([])
  })

  it('puts the session, message, record and product ID into the preview media URL', () => {
    expect(previewAttachmentFileUrl('s 1', 'm/2', NODE_A, 'a4')).toBe(
      `/api/web/chat/sessions/s%201/messages/m%2F2/preview/records/${NODE_A}/attachments/a4/file`
    )
  })
})

describe('Agent approval preview panel', () => {
  const html = (diff: Parameters<typeof preview>[0], records: ViewRecord[]) =>
    renderToStaticMarkup(createElement(PreviewAssetChanges, { preview: preview(diff), records }))

  it('spells out the changed products and references of added, updated and removed nodes', () => {
    const before = record(NODE_A, '原作分镜', 10, {
      attachments: [attachment('a2', '改前')],
      references: [reference('e1', 'v1')]
    })
    const after = record(NODE_A, '原作分镜', 10, {
      attachments: [attachment('a2', '改后'), attachment('a3', '新图')],
      references: [reference('e1', 'v1', 'ver2', '沈砚', '少年黑衣', [{ id: 'ver2', number: 2, label: 'v2' }])]
    })
    const removed = record(NODE_C, '待删节点', 300, {
      attachments: [attachment('c1', '旧片', 'Video')],
      references: [reference('e4', 'v1', null, '旧书店')]
    })
    const added = record(NODE_B, '新节点', 200, {
      attachments: [attachment('b1', '新角色图')],
      references: [reference('e2', 'v1', null, '林夏')]
    })
    const markup = html({ addedNodes: [added], updatedNodes: [after], removedNodes: [removed] }, [before, removed])

    expect(markup).toContain('<details')
    expect(markup).toContain('附件 / 引用变更详情')
    expect(markup).toContain('＋ 新增节点')
    expect(markup).toContain('△ 修改节点')
    expect(markup).toContain('− 移除节点')
    // 新增的产物还没进正式节点：图与文字都要给，且图走「按本批动作重新试算」的那条预览接口。
    expect(markup).toContain('新角色图')
    expect(markup).toContain('新图')
    expect(markup).toContain('名称：改前 → 改后')
    expect(markup).toContain(`/preview/records/${NODE_B}/attachments/b1/file`)
    // 变更前就在的产物（含随节点删除的那些）走正式那条按 ID 取字节的接口。
    expect(markup).toContain(`/api/web/records/${NODE_C}/attachments/c1/file`)
    expect(markup).toContain('查看视频')
    // 引用：实体 · 变体 · 锁定版本 / 跟随最新，前后对照一起给。
    expect(markup).toContain('林夏')
    expect(markup).toContain('旧书店')
    expect(markup).toContain('版本：跟随最新 → 锁定版本 v2')
    expect(markup).toContain('只读展示')
  })

  it('says so instead of showing anything when only node text changed', () => {
    const same = { attachments: [attachment('a1', '甲')], references: [reference('e1', 'v1')] }
    const markup = html({ updatedNodes: [record(NODE_D, '只改了文字', 10, { ...same })] }, [record(NODE_D, '只改了文字', 10, { ...same })])
    expect(markup).toContain('本次审批没有附件或引用的变化')
    expect(markup).not.toContain('<details')
  })
})
