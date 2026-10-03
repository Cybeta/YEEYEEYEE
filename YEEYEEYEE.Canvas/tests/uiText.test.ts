import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { clientLabel } from '../src/shell/locks'
import { gestureHint, uiText, uiTextFill, uiTextKeys } from '../src/shell/uiText'

/**
 * 仓库里所有 C# 源码（跳过 node_modules / bin / obj / .git）。
 * 用来把「C# 里引用的键」与共享文件对一遍——这个仓库里 C# 与 TS 谁都不知道对方的字面量。
 */
function csharpSources(directory: URL, found: URL[] = []): URL[] {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (['node_modules', 'bin', 'obj', '.git'].includes(entry.name)) continue
    const child = new URL(`${entry.name}${entry.isDirectory() ? '/' : ''}`, directory)
    if (entry.isDirectory()) csharpSources(child, found)
    else if (entry.name.endsWith('.cs')) found.push(child)
  }
  return found
}

/**
 * 两端共读的界面文案（画布手势提示这一片）。
 *
 * 重点不在「文案写得对不对」，而在**只有一份**：桌面端 csproj 里嵌的必须是同一个文件
 * （最后一条直接去读 csproj 对账）。两端的措辞与分隔符过去各写一份，已经走散过——
 * 一边说「点节点选中」，一边说「点击选中」；一边用「 · 」，一边用「 / 」。
 */
describe('共享界面文案', () => {
  it('措辞与分隔符一起共享，随手拼都不会拼出两种样子', () => {
    expect(uiText('separator')).toBe(' · ')
    expect(gestureHint('gesture.pan', 'gesture.select')).toBe('空白处拖拽平移 · 点节点或连线选中')
    expect(gestureHint('connect.mode', 'gesture.cancel')).toBe('连接模式：点另一个节点作为终点 · Esc 取消')
  })

  it('两端要用的那几句都在（少一句，总有一边会空一截）', () => {
    for (const key of [
      'gesture.pan', 'gesture.zoom', 'gesture.dragNode', 'gesture.connect',
      'gesture.select', 'gesture.delete', 'gesture.cancel', 'connect.mode'
    ]) {
      expect(uiText(key).length).toBeGreaterThan(0)
    }
  })

  it('键写错就当场炸，不静默少一截', () => {
    expect(() => uiText('gesture.nope')).toThrow(/没有这个键/)
    expect(uiTextKeys()).not.toContain('gesture.nope')
  })

  it('画布提示条真的走共享文案，不再在视图里写死一句', () => {
    const view = readFileSync(new URL('../src/shell/CanvasView.tsx', import.meta.url), 'utf8')
    expect(view).toContain("gestureHint('gesture.pan'")
    // 三个只会出现在那一行里的老措辞；「滚轮缩放」之类别的地方也在用，不作数。
    for (const literal of ['拖空白平移', '拖右缘圆点连线', '拖节点挪位置', '连接模式：再点一个节点作为终点'])
      expect(view).not.toContain(literal)
  })

  it('「谁在编辑」那一句的措辞与来源端说法也走同一份（三处过去各写一遍，注释互相指着）', () => {
    expect(clientLabel('desktop')).toBe(uiText('lease.client.desktop'))
    expect(clientLabel('web')).toBe(uiText('lease.client.web'))
    expect(clientLabel('unknown')).toBe('网页端')
    expect(uiTextFill('lease.who', { name: '陈默', client: clientLabel('desktop') })).toBe('陈默（桌面端）')

    // locks.ts 里不该再出现写死的标签——注释里提到「桌面端」没关系，被引号包起来才算抄了一份。
    const locks = readFileSync(new URL('../src/shell/locks.ts', import.meta.url), 'utf8')
    expect(locks).not.toContain("'桌面端'")
    expect(locks).not.toContain("'网页端'")
  })

  it('面板标签两端同措辞：一端改了名字，另一端不会再悄悄留着旧名字', () => {
    for (const key of ['panel.canvas', 'panel.timeline', 'panel.script', 'panel.projectTree', 'panel.storyCanvas'])
      expect(uiText(key).length).toBeGreaterThan(0)

    // 网页端：标签走共享文案，视图里不再写死。
    const shell = readFileSync(new URL('../src/shell/WorkbenchShell.tsx', import.meta.url), 'utf8')
    expect(shell).toContain("uiText('panel.canvas')")
    expect(shell).toContain("uiText('panel.storyCanvas')")
    for (const literal of ["label: '画布'", "label: '时间轴'", "label: '剧本'", "label: '项目树'", "label: '故事画布'"])
      expect(shell).not.toContain(literal)

    // 桌面端：那几个 Content 改成 {x:Static} 绑共享文案，不再是手抄的字面量。
    const axaml = readFileSync(
      new URL('../../YEEYEEYEE.Desktop.Avalonia/MainWindow.axaml', import.meta.url), 'utf8')
    expect(axaml).toContain('Content="{x:Static shared:UiText.PanelCanvas}"')
    expect(axaml).toContain('Content="{x:Static shared:UiText.PanelStoryCanvas}"')
    for (const literal of ['Content="画布"', 'Content="时间轴"', 'Content="剧本"', 'Content="项目树"', 'Content="故事画布"'])
      expect(axaml).not.toContain(literal)
  })

  it('检查器那两句提示（含 Ctrl+Enter 这个快捷键）只有一份，三处都读它', () => {
    expect(uiText('inspector.applyHint')).toContain('Ctrl+Enter')
    // 初值那句就是前一句前面多了「选中节点后可改；」——分开住但必须同源，改一处别忘另一处。
    expect(uiText('inspector.idleHint')).toContain(uiText('inspector.applyHint'))

    // 三处写法：XAML 的初值、.cs 的状态行、网页端。谁再抄一遍这句，这里当场红。
    for (const relative of [
      '../../YEEYEEYEE.Desktop.Avalonia/MainWindow.axaml',
      '../../YEEYEEYEE.Desktop.Avalonia/MainWindow.axaml.cs',
      '../src/WebCanvasApp.tsx'
    ]) {
      const source = readFileSync(new URL(relative, import.meta.url), 'utf8')
      expect(source, relative).not.toContain('按 Ctrl+Enter 写回画布')
    }

    const axaml = readFileSync(
      new URL('../../YEEYEEYEE.Desktop.Avalonia/MainWindow.axaml', import.meta.url), 'utf8')
    expect(axaml).toContain('{x:Static shared:UiText.InspectorIdleHint}')
    const web = readFileSync(new URL('../src/WebCanvasApp.tsx', import.meta.url), 'utf8')
    expect(web).toContain("uiText('inspector.applyHint')")
  })

  it('占位符填不干净就抛出，不让界面显示半句话', () => {
    expect(() => uiTextFill('lease.who', { name: '陈默' })).toThrow(/占位符/)
  })

  it('C# 里引用的每个键都真的存在（键名对不上，只有跑到那一行才发现）', () => {
    const root = new URL('../../', import.meta.url)
    const referenced = new Set<string>()
    for (const file of csharpSources(root)) {
      const source = readFileSync(file, 'utf8')
      for (const match of source.matchAll(/UiText\.(?:Text|Fill)\(\s*"([^"]+)"/g)) referenced.add(match[1])
      for (const match of source.matchAll(/UiText\.Gestures\(([^)]*)\)/g))
        for (const key of match[1].matchAll(/"([^"]+)"/g)) referenced.add(key[1])
    }

    // 扫到 0 个说明扫描本身失效了（比如 C# 挪了地方），那这条测试就成了摆设。
    expect(referenced.size).toBeGreaterThan(5)
    for (const key of [...referenced].sort()) expect(uiTextKeys(), key).toContain(key)
  })

  it('桌面端连线手势那几句已经改成读共享文案，没留下写死的副本', () => {
    const surface = readFileSync(
      new URL('../../YEEYEEYEE.Desktop.Avalonia/CanvasSurface.axaml.cs', import.meta.url), 'utf8')
    for (const literal of ['请选择连接起点', '已选择起点：', '不能连到自己', '松在空白处', '拖拽已结束'])
      expect(surface).not.toContain(literal)
    expect(surface).toContain('UiText.Fill("connect.pickedSource"')
  })

  it('C# 那边嵌的是同一个文件，而且**只有一处**嵌它（再嵌一份就又变回两份抄写）', () => {
    const root = new URL('../../', import.meta.url)
    const embed = 'EmbeddedResource Include="..\\YEEYEEYEE.Canvas\\src\\shared\\uiText.json"'
    const embedding = readdirSync(root, { withFileTypes: true })
      .filter((entry) => entry.isDirectory())
      .map((entry) => `${entry.name}/${entry.name}.csproj`)
      .filter((relative) => existsSync(new URL(relative, root)))
      .filter((relative) => readFileSync(new URL(relative, root), 'utf8').includes(embed))
    expect(embedding).toEqual(['YEEYEEYEE.Desktop.Shared/YEEYEEYEE.Desktop.Shared.csproj'])
  })
})
