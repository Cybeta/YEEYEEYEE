import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import protocol from '../src/shared/protocol.json'
import fixtures from '../src/shared/protocolFixtures.json'

/**
 * 画布 ↔ 宿主的消息表与它的固定样本。
 *
 * 重点不在「表里写了什么」，而在**只有一份**、而且**网页端没有另抄一份**：
 * 两端各存一份表的时候，先对不上的那次只会表现成「某条消息莫名被判为未知类型」——
 * 没有人会想到去查协议表。同样的道理，样本里「什么算合法」也只能存在一处，
 * 否则两份实现各自理解了一遍「必填」。
 *
 * C# 那边由 `YEEYEEYEE.Core.Tests` 照同一份文件验（含「嵌进去的逐字节等于源码树里那份」）。
 */

type Message = { type: string; direction: string; required: string[] }
type Case = { name: string; ok: boolean; code?: string; direction?: string; payload: Record<string, unknown> }
type Section = { type: string; direction: string; cases: Case[] }

const messages = protocol.messages as Message[]
const allowedDirections = protocol.directions as string[]
// 经 unknown 转一道：两个 section 的 payload 形状本来就不同，直接 as 会被 TS 判为「不像话的转换」。
const sections = [fixtures.request, fixtures.result] as unknown as Section[]

/** 包里所有 TS / TSX 源码（跳过 node_modules）。 */
function tsSources(directory: URL, found: URL[] = []): URL[] {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (entry.name === 'node_modules') continue
    const child = new URL(`${entry.name}${entry.isDirectory() ? '/' : ''}`, directory)
    if (entry.isDirectory()) tsSources(child, found)
    else if (entry.name.endsWith('.ts') || entry.name.endsWith('.tsx')) found.push(child)
  }
  return found
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
/** 载荷里这些字段必须是 UUID（`variantVersionId` 例外：null 表示解除锁定）。 */
const UUID_FIELDS = ['recordId', 'entityId', 'variantId', 'variantVersionId', 'requestId', 'jobId', 'invocationId', 'batchId']

describe('画布与宿主的消息表', () => {
  it('表自洽：类型不重复、方向只有那两个、必填字段都是字段名', () => {
    expect(protocol.version).toBeGreaterThan(0)
    expect([...allowedDirections].sort()).toEqual(['canvasToHost', 'hostToCanvas'])
    expect(messages.length).toBeGreaterThanOrEqual(10)

    const seen = new Set<string>()
    for (const message of messages) {
      expect(message.type, '类型不能空').toMatch(/^(canvas|host)\/[a-z.]+$/)
      expect(seen.has(message.type), `${message.type} 写了两遍`).toBe(false)
      seen.add(message.type)
      expect(allowedDirections, `${message.type} 的方向不在允许的两个里`).toContain(message.direction)
      // 类型前缀与方向得对得上：写反了不会报错，只会在运行期被判成「方向错误」。
      expect(message.direction, `${message.type} 的前缀与方向对不上`).toBe(
        message.type.startsWith('canvas/') ? 'canvasToHost' : 'hostToCanvas'
      )
      expect(Array.isArray(message.required)).toBe(true)
      for (const field of message.required) expect(typeof field).toBe('string')
    }
  })

  it('固定样本都在表里，而且正例把必填字段写齐了', () => {
    let cases = 0
    for (const section of sections) {
      const message = messages.find((item) => item.type === section.type)
      expect(message, `${section.type} 不在表里`).toBeTruthy()
      expect(message!.direction, `${section.type} 的方向与表里不一致`).toBe(section.direction)

      for (const item of section.cases) {
        cases++
        const fields = Object.keys(item.payload)
        const missing = message!.required.filter((field) => !fields.includes(field))
        // 一个样本可以因为三种原因不合法：少了必填字段、标识字段不是 UUID、方向反了。
        const wrongDirection = item.direction !== undefined && item.direction !== section.direction

        if (item.ok) expect(missing, `正例「${item.name}」少了必填字段`).toEqual([])
        else
          expect(missing.length > 0 || hasBadIdentifier(item.payload) || wrongDirection,
            `反例「${item.name}」得真的不合法`).toBe(true)

        // 正例里该是 UUID 的字段必须真是 UUID（反例不在这里断言：它们的不合法已经由上面那条兜住）。
        for (const field of UUID_FIELDS) {
          if (!fields.includes(field)) continue
          const value = item.payload[field]
          if (field === 'variantVersionId' && value === null) continue
          if (!item.ok) continue
          expect(typeof value === 'string' && UUID.test(value), `正例「${item.name}」的 ${field} 不是 UUID`).toBe(true)
        }
      }
    }
    expect(cases).toBeGreaterThanOrEqual(8)
  })

  it('网页端没有另抄一份消息表（有的话，先对不上的那次没人会去查）', () => {
    const literals = new Set(messages.map((message) => message.type))
    const offenders: string[] = []
    for (const file of tsSources(new URL('../src/', import.meta.url))) {
      const source = readFileSync(file, 'utf8')
      for (const literal of literals) if (source.includes(`'${literal}'`) || source.includes(`"${literal}"`)) offenders.push(`${file.pathname}: ${literal}`)
    }
    // 扫到 0 个类型说明扫描本身失效了，那这条测试就成了摆设。
    expect(literals.size).toBeGreaterThanOrEqual(10)
    expect(offenders).toEqual([])
  })

  it('只有一处 csproj 嵌着协议表，而且 Dockerfile 把共享目录拷进了构建上下文', () => {
    const root = new URL('../../', import.meta.url)
    const embed = 'EmbeddedResource Include="..\\YEEYEEYEE.Canvas\\src\\shared\\protocol.json"'
    const embedding = readdirSync(root, { withFileTypes: true })
      .filter((entry) => entry.isDirectory())
      .map((entry) => `${entry.name}/${entry.name}.csproj`)
      .filter((relative) => existsSync(new URL(relative, root)))
      .filter((relative) => readFileSync(new URL(relative, root), 'utf8').includes(embed))
    expect(embedding).toEqual(['YEEYEEYEE.Core/YEEYEEYEE.Core.csproj'])

    // 这条链会在镜像里构建（Web → Core）：「本机照过、镜像构建才炸」那种错只在这一行上防得住。
    const dockerfile = readFileSync(new URL('Dockerfile', root), 'utf8')
    expect(dockerfile).toContain('COPY YEEYEEYEE.Canvas/src/shared/ YEEYEEYEE.Canvas/src/shared/')
  })
})

/** 载荷里有「存在但不是 UUID」的标识字段——那正是反例要验的不合法。 */
function hasBadIdentifier(payload: Record<string, unknown>): boolean {
  for (const field of UUID_FIELDS) {
    const value = payload[field]
    if (field === 'variantVersionId' && (value === null || value === undefined)) continue
    if (value === undefined) continue
    if (typeof value === 'string' && UUID.test(value)) continue
    return true
  }
  return false
}
