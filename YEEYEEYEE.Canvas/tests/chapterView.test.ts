import { describe, expect, it } from 'vitest'
import {
  chapterEntries, chapterIdOf, chapterLabelOf, chapterOrderOf,
  isPlanningLayer, sortWithinChapters, type ViewRecord
} from '../src/ChapterView'

function node(id: string, recordType: string, record: Record<string, unknown>): ViewRecord {
  return { recordId: id, recordType, record }
}

const chapterA = '11111111-1111-4111-8111-111111111111'
const chapterB = '22222222-2222-4222-8222-222222222222'

describe('章节视图按稳定 ID 对齐（C-4）', () => {
  it('章节身份取稳定 ID，不用同名文本', () => {
    const record = node('n1', 'storyboard', { title: '分镜', chapter: '第1章', chapterId: chapterA })
    expect(chapterIdOf(record)).toBe(chapterA)
    expect(chapterLabelOf(record)).toBe('第1章')

    // 只有章节名、没有稳定 ID：不参与章节绑定。
    const textOnly = node('n2', 'storyboard', { title: '分镜', chapter: '第1章' })
    expect(chapterIdOf(textOnly)).toBe('')
  })

  it('同名不同 ID 的章节是两条，不会被合并', () => {
    const records = [
      node('c1', 'chapter', { title: '第1章', workTreeItemId: chapterA, order: 10 }),
      node('c2', 'chapter', { title: '第1章', workTreeItemId: chapterB, order: 20 })
    ]
    const entries = chapterEntries(records)
    expect(entries.map((entry) => entry.id)).toEqual([chapterA, chapterB])
    expect(entries.every((entry) => entry.label === '第1章')).toBe(true)
  })

  it('章节按显式顺序排列，不按名称里的数字', () => {
    const records = [
      node('c1', 'chapter', { title: '第十章', workTreeItemId: chapterA, order: 10 }),
      node('c2', 'chapter', { title: '第2章', workTreeItemId: chapterB, order: 20 })
    ]
    // 显式 order 决定顺序：order 10 在前，即使名字里是「第十章」。
    expect(chapterEntries(records).map((entry) => entry.id)).toEqual([chapterA, chapterB])
    expect(chapterOrderOf(records[0])).toBe(10)
  })

  it('按稳定 ID 归属：章节名相同也不会串，没挂 ID 的不属于任何章节', () => {
    const records = [
      node('plan', 'story-plan', { title: '剧情源' }),
      node('a1', 'storyboard', { title: 'A 分镜', chapter: '第1章', chapterId: chapterA }),
      node('b1', 'storyboard', { title: 'B 分镜', chapter: '第1章', chapterId: chapterB }),
      node('free', 'storyboard', { title: '未归档分镜', chapter: '第1章' })
    ]
    // 两条分镜的章节**名**一模一样，但归属看 ID：它们不是同一章。
    expect(records.filter((record) => chapterIdOf(record) === chapterB).map((record) => record.recordId)).toEqual(['b1'])
    // 只有名字、没有 ID 的，不属于任何章节——也不会因为名字相同被算进那一章。
    expect(records.filter((record) => chapterIdOf(record) === '').map((record) => record.recordId)).toEqual(['plan', 'free'])
  })

  /** 第 181 轮删掉 `filterByChapter` 之后，这套「归属」判据只剩 `chapterIdOf` 这一个原语（外壳也用它）。 */
  it('归属只看 ID，与节点是第几层无关', () => {
    const records = [
      node('hero', 'Character', { title: '林晚', chapterId: chapterA }),
      node('b1', 'storyboard', { title: 'B 分镜', chapterId: chapterB })
    ]
    // 角色挂在这一章上就在这一章里——它是不是「资源」不影响归属。
    expect(records.filter((record) => chapterIdOf(record) === chapterA).map((record) => record.recordId)).toEqual(['hero'])
  })

  /**
   * 这一条曾经是错的：判据写成 `layerOf(...) <= 2`，而 `layerOf` 对认不出来的类型返回 0，
   * 于是角色 / 场景 / 道具 / 通用这些真实节点全被判成「企划层」——选第一章时它们赖在画面上不走，
   * 看起来像「第一章里有三个角色」；剧本视图里还会被排到最前。
   * 判据本身是活的：外壳的 `records.isPlanning` 直接用它，与桌面端泳道引擎的
   * `CanvasSwimlaneLayout.IsPlanningCategory` 是同一条规则。
   * （`filterByChapter` 那几个用例只覆盖函数自己：第 181 轮删掉 WebView 那份前端之后它已无生产调用方
   *   ——网页端选某一章是**压暗**而不是筛掉。要清掉它得单独一次。）
   */
  it('企划层只认 L1 剧情与 L2 企划：认不出的类型（0）不是企划层', () => {
    expect(isPlanningLayer(node('p', 'story-plan', {}))).toBe(true)
    expect(isPlanningLayer(node('o', 'story-outline', {}))).toBe(true)
    // 这三类是「资源」，引擎会把它们放进未分章泳道，而不是企划区。
    expect(isPlanningLayer(node('c', 'Character', {}))).toBe(false)
    expect(isPlanningLayer(node('s', 'Scene', {}))).toBe(false)
    expect(isPlanningLayer(node('r', 'Prop', {}))).toBe(false)
    // 认不出来的类型同样是 0：0 是「不知道」，不是「企划」。
    expect(isPlanningLayer(node('x', 'something-new', {}))).toBe(false)
  })

  it('章节内排序稳定：先工作树顺序，再坐标，最后标题', () => {
    const ordered = sortWithinChapters([
      node('late', 'storyboard', { title: '后', chapterId: chapterA, order: 30 }),
      node('early', 'storyboard', { title: '前', chapterId: chapterA, order: 10 }),
      node('free', 'storyboard', { title: '未归档' })
    ])
    expect(ordered.map((record) => record.recordId)).toEqual(['early', 'late', 'free'])
  })
})
