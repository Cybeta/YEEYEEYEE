import { describe, expect, it } from 'vitest'
import { parseReferenceOption, referenceOptions, type Asset } from '../src/assets'

/**
 * 挂设定时那个下拉里的选项（第 176 轮）。
 *
 * 两条容易写错、又不会报错的规矩：实体**没有变体**时也得给出一行（服务端会落到第一个变体），
 * 以及标签里的名字只是显示用——**身份始终是 ID**。后者错了会「看着挑的是林晚，其实挂的是别人」，
 * 所以 value 里带的是 ID，不是名字。
 */
describe('挂设定的下拉选项', () => {
  const assets: Asset[] = [
    { id: 'e1', name: '林晚', variants: [{ id: 'v1', name: '默认' }, { id: 'v2', name: '雨夜' }] },
    { id: 'e2', name: '码头', variants: [] },
    { id: 'e3', name: '无变体字段' }
  ]

  it('每个实体 × 每个变体一行；没有变体的实体也要给一行', () => {
    expect(referenceOptions(assets)).toEqual([
      { value: 'e1|v1', label: '林晚 · 默认', entityId: 'e1', variantId: 'v1' },
      { value: 'e1|v2', label: '林晚 · 雨夜', entityId: 'e1', variantId: 'v2' },
      { value: 'e2', label: '码头', entityId: 'e2', variantId: null },
      { value: 'e3', label: '无变体字段', entityId: 'e3', variantId: null }
    ])
  })

  it('变体没名字时标签只有实体名——不留一个孤零零的「·」', () => {
    expect(referenceOptions([{ id: 'e4', name: '雾', variants: [{ id: 'v9' }] }])[0].label).toBe('雾')
  })

  it('value 拆得回去；拆不出实体 ID 就是 null（那说明这个 value 不是这里生成的）', () => {
    expect(parseReferenceOption('e1|v1')).toEqual({ entityId: 'e1', variantId: 'v1' })
    expect(parseReferenceOption('e2')).toEqual({ entityId: 'e2', variantId: null })
    expect(parseReferenceOption('')).toBeNull()
  })
})
