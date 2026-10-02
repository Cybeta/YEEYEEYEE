import { describe, expect, it } from 'vitest'
import {
  ALL_CHAPTERS_ID, chapterEntries, chapterIdOf, chapterLabelOf, chapterOrderOf,
  filterByChapter, sortWithinChapters, type ViewRecord
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

  it('按 ID 筛选：同名文本不会串章节，企划层始终保留', () => {
    const records = [
      node('plan', 'story-plan', { title: '剧情源' }),
      node('a1', 'storyboard', { title: 'A 分镜', chapter: '第1章', chapterId: chapterA }),
      node('b1', 'storyboard', { title: 'B 分镜', chapter: '第1章', chapterId: chapterB }),
      node('free', 'storyboard', { title: '未归档分镜', chapter: '第1章' })
    ]
    const filtered = filterByChapter(records, chapterB)
    expect(filtered.map((record) => record.recordId)).toEqual(['plan', 'b1'])
    expect(filterByChapter(records, ALL_CHAPTERS_ID)).toHaveLength(4)
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
