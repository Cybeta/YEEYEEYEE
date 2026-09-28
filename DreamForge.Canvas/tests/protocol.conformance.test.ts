import { describe, expect, it } from 'vitest'
import { readdirSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { decode, ProtocolViolationError } from '../src/Protocol/CanvasMessageCodec'
import type { Direction } from '../src/Protocol/VersionedMessages'

describe('protocol fixtures', () => { const root = resolve(process.cwd(), '..', 'protocol', 'fixtures'); const manifest = JSON.parse(readFileSync(resolve(root, 'manifest.json'), 'utf8')) as { fixtures: Array<{ file: string; direction: Direction; valid: boolean; expectedErrorCode?: string }> }; for (const fixture of manifest.fixtures) it(fixture.file, () => { const message = JSON.parse(readFileSync(resolve(root, fixture.file), 'utf8')); if (fixture.valid) expect(() => decode(message, fixture.direction)).not.toThrow(); else { try { decode(message, fixture.direction); throw new Error('未抛出协议错误') } catch (error) { expect(error).toBeInstanceOf(ProtocolViolationError); expect((error as ProtocolViolationError).code).toBe(fixture.expectedErrorCode) } } }) })
