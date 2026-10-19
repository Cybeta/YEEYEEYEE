import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { CSSProperties } from 'react'
import { withRetry } from './assetImages'
import {
  GACHA_TIMING,
  averageTint,
  canConfirmGacha,
  gachaBatchProgress,
  gachaCardWidth,
  gachaDeck,
  gachaNextSelection,
  gachaOffset,
  gachaPerRow,
  gachaRows,
  gachaSelectionNote,
  gachaStatusLine,
  gachaTimeline,
  type GachaCard,
  type GachaDeck,
  type GachaBatchProgress
} from './gachaRules'
import type { CandidateSlotView } from './candidateBatch'
import {
  QUALITY_DISCLAIMER,
  UNGRADED_SPARKS,
  qualityBadge,
  qualityIsCracked,
  qualityIsGraded,
  qualityOmen,
  qualitySparks
} from './imageQuality'

/**
 * 网页端「开奖」全屏揭晓。
 *
 * 视觉与时序都照 Desktop 的 GachaRevealDialog：蓄势 → 爆发 → 飞卡 → 依次翻面 → 挑一张。
 * 但只把**表现**搬过来，不引入 Avalonia 的控件与依赖；配色、卡背、光晕、选中效果用的是同一套语言。
 *
 * 判档结果随牌带进来：有档位就按 Desktop 的档位牌、裂纹与选中说明展示；
 * **没有结果就写「未判档」**，不编分数，也不把没判过的图当成白档。
 *
 * 另一处明确的范围：不删除未采用的文件，采用成功后只收起这一批候选。
 */

/** 揭晓自己的阶段。`loading` 与 `loadError` 不播放成功动画。 */
export type GachaPhase = 'loading' | 'wish' | 'burst' | 'cards' | 'flips' | 'ready' | 'loadError'

type StyleVars = CSSProperties & Record<string, string | number>

export type GachaRevealViewProps = {
  nodeTitle: string
  progress: GachaBatchProgress
  deck: GachaDeck
  phase: GachaPhase
  /** 预加载失败的那几张：仍然列在牌堆里，但明确说明读不出来、不能选。 */
  loadFailed: ReadonlySet<string>
  tints: Record<string, string>
  loadedCount: number
  selection: number | null
  adopting: boolean
  error: string
  /** 判档开关：关着时不显示服务端可能已有的档位，卡面保持「未判档」。 */
  showQuality?: boolean
  /** 判档还没结束 / 被跳过 / 失败时的一句话；不影响开奖与采用。 */
  qualityNote?: string
  onSelect: (index: number) => void
  onAdopt: (card: GachaCard) => void
  onClose: () => void
  onSkip: () => void
  onRetry: () => void
}

/** 把一张图的 24×24 缩略图按色相分桶，取最主要的那个色相（与 Desktop 同一思路）。 */
function readTint(image: HTMLImageElement, fallback: string): string {
  try {
    const canvas = document.createElement('canvas')
    canvas.width = 24
    canvas.height = 24
    const context = canvas.getContext('2d', { willReadFrequently: true })
    if (!context) return fallback
    context.drawImage(image, 0, 0, 24, 24)
    const data = context.getImageData(0, 0, 24, 24).data
    const buckets = new Array<number>(12).fill(0)
    for (let i = 0; i < data.length; i += 4) {
      const r = data[i] / 255
      const g = data[i + 1] / 255
      const b = data[i + 2] / 255
      const max = Math.max(r, g, b)
      const min = Math.min(r, g, b)
      const saturation = max <= 0 ? 0 : (max - min) / max
      if (saturation < 0.18 || max < 0.12) continue
      let hue = 0
      if (max === r) hue = ((g - b) / (max - min)) % 6
      else if (max === g) hue = (b - r) / (max - min) + 2
      else hue = (r - g) / (max - min) + 4
      hue = ((hue * 60) + 360) % 360
      const bucket = Math.min(11, Math.floor(hue / 30))
      buckets[bucket] += saturation * max
    }
    let best = 0
    for (let i = 1; i < buckets.length; i++) if (buckets[i] > buckets[best]) best = i
    if (buckets[best] === 0) return fallback
    return hslToHex(best * 30 + 15, 0.62, 0.58)
  } catch {
    return fallback
  }
}

function hslToHex(hue: number, saturation: number, lightness: number): string {
  const c = (1 - Math.abs(2 * lightness - 1)) * saturation
  const x = c * (1 - Math.abs(((hue / 60) % 2) - 1))
  const m = lightness - c / 2
  const [r, g, b] =
    hue < 60 ? [c, x, 0]
      : hue < 120 ? [x, c, 0]
        : hue < 180 ? [0, c, x]
          : hue < 240 ? [0, x, c]
            : hue < 300 ? [x, 0, c] : [c, 0, x]
  const hex = (value: number) => Math.round((value + m) * 255).toString(16).padStart(2, '0')
  return `#${hex(r)}${hex(g)}${hex(b)}`
}

function useReducedMotion(): boolean {
  const [reduced, setReduced] = useState(() =>
    typeof window !== 'undefined' && typeof window.matchMedia === 'function'
      && window.matchMedia('(prefers-reduced-motion: reduce)').matches)
  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return
    const query = window.matchMedia('(prefers-reduced-motion: reduce)')
    const onChange = () => setReduced(query.matches)
    query.addEventListener?.('change', onChange)
    return () => query.removeEventListener?.('change', onChange)
  }, [])
  return reduced
}

const FALLBACK_TINT = '#4d9bff'

/**
 * 有副作用的那一层：预加载、计时器、键盘、选中。
 * 纯展示交给 <see cref="GachaRevealView"/>，这样各个阶段都能用 SSR 断言到。
 */
export function GachaReveal(props: {
  nodeTitle: string
  slots: CandidateSlotView[]
  selection: number | null
  adopting: boolean
  error: string
  showQuality: boolean
  qualityNote: string
  onSelect: (index: number) => void
  onAdopt: (card: GachaCard) => void
  onClose: () => void
}) {
  const progress = useMemo(() => gachaBatchProgress(props.slots), [props.slots])
  const deck = useMemo(() => gachaDeck(props.slots), [props.slots])
  const reducedMotion = useReducedMotion()
  const [phase, setPhase] = useState<GachaPhase>('loading')
  const [loadFailed, setLoadFailed] = useState<ReadonlySet<string>>(new Set())
  const [tints, setTints] = useState<Record<string, string>>({})
  const [loadedCount, setLoadedCount] = useState(0)
  const [reload, setReload] = useState(0)

  const cardsRef = useRef(deck.cards)
  cardsRef.current = deck.cards
  // props.slots 是打开揭晓时的快照；这个串只在批次内容真的变了时才变，用来钉住预加载 effect。
  const cardsKey = deck.cards.map(card => `${card.key}:${card.imageUrl}`).join('|')
  const timersRef = useRef<number[]>([])
  const submitRef = useRef(false)
  const stateRef = useRef({ phase, selection: props.selection, adopting: props.adopting, loadFailed, deck })
  stateRef.current = { phase, selection: props.selection, adopting: props.adopting, loadFailed, deck }

  useEffect(() => {
    if (!props.adopting) submitRef.current = false
  }, [props.adopting])

  const clearTimers = useCallback(() => {
    for (const timer of timersRef.current) window.clearTimeout(timer)
    timersRef.current = []
  }, [])

  // 预加载：全部落定之后才开动画。任何一张超时或失败都算落定——不把界面永久卡在「加载中」。
  useEffect(() => {
    let cancelled = false
    const images: HTMLImageElement[] = []
    const loadTimers: number[] = []
    const finished = new Set<string>()
    clearTimers()
    setPhase('loading')
    setLoadFailed(new Set())
    setLoadedCount(0)

    const cards = cardsRef.current
    if (cards.length === 0) {
      setPhase('loadError')
      return
    }

    const complete = () => {
      if (cancelled) return
      const failedKeys = new Set(failures)
      setLoadFailed(failedKeys)
      if (failedKeys.size >= cards.length) {
        setPhase('loadError')
        return
      }
      if (reducedMotion) {
        setPhase('ready')
        return
      }
      setPhase('wish')
      const { flipsAt, allDoneAt } = gachaTimeline(cards.length)
      timersRef.current = [
        window.setTimeout(() => setPhase('burst'), GACHA_TIMING.burstMs),
        window.setTimeout(() => setPhase('cards'), GACHA_TIMING.cardsFlyAtMs),
        window.setTimeout(() => setPhase('flips'), flipsAt),
        window.setTimeout(() => setPhase('ready'), allDoneAt)
      ]
    }

    const failures = new Set<string>()
    let settled = 0
    const nextTints: Record<string, string> = {}

    for (const card of cards) {
      const image = new Image()
      images.push(image)
      const timer = window.setTimeout(() => finish(card.key, false, FALLBACK_TINT), 15_000)
      loadTimers.push(timer)
      const finish = (key: string, ok: boolean, tint: string) => {
        window.clearTimeout(timer)
        if (cancelled || finished.has(key)) return
        finished.add(key)
        settled++
        if (ok) nextTints[key] = tint
        else failures.add(key)
        setLoadedCount(settled)
        if (settled >= cards.length) {
          setTints(nextTints)
          complete()
        }
      }
      image.onload = () => finish(card.key, true, readTint(image, FALLBACK_TINT))
      image.onerror = () => finish(card.key, false, FALLBACK_TINT)
      image.src = withRetry(card.imageUrl, reload)
    }

    return () => {
      cancelled = true
      clearTimers()
      for (const timer of loadTimers) window.clearTimeout(timer)
      for (const image of images) {
        image.onload = null
        image.onerror = null
      }
    }
  }, [cardsKey, reload, reducedMotion, clearTimers])

  const skip = useCallback(() => {
    clearTimers()
    setPhase('ready')
  }, [clearTimers])

  const confirm = useCallback(() => {
    const current = stateRef.current
    if (!canConfirmGacha({
      selection: current.selection,
      cards: current.deck.cards,
      loadFailed: current.loadFailed,
      adopting: current.adopting
    })) return
    if (submitRef.current) return
    submitRef.current = true
    props.onAdopt(current.deck.cards[current.selection!])
  }, [props.onAdopt])

  const close = useCallback(() => {
    if (stateRef.current.adopting) return
    props.onClose()
  }, [props.onClose])

  // 键盘：Esc 关闭、左右选、回车采用。全屏没有鼠标时也能走完这一趟。
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const current = stateRef.current
      if (event.key === 'Escape') {
        event.preventDefault()
        close()
        return
      }
      if (current.phase !== 'ready') return
      if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
        const next = gachaNextSelection(current.selection, event.key === 'ArrowLeft' ? -1 : 1, current.deck.cards.length)
        if (next >= 0) {
          event.preventDefault()
          props.onSelect(next)
        }
        return
      }
      if (event.key === 'Enter') {
        event.preventDefault()
        confirm()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [close, confirm, props.onSelect])

  useEffect(() => () => clearTimers(), [clearTimers])

  return (
    <GachaRevealView
      nodeTitle={props.nodeTitle}
      progress={progress}
      deck={deck}
      phase={phase}
      loadFailed={loadFailed}
      tints={tints}
      loadedCount={loadedCount}
      selection={props.selection}
      adopting={props.adopting}
      error={props.error}
      showQuality={props.showQuality}
      qualityNote={props.qualityNote}
      onSelect={props.onSelect}
      onAdopt={props.onAdopt}
      onClose={close}
      onSkip={skip}
      onRetry={() => setReload(value => value + 1)}
    />
  )
}

/** 纯展示层：阶段由外部给，测试可以直接摆到任意一拍。 */
export function GachaRevealView(props: GachaRevealViewProps) {
  const { progress, deck, phase, selection, adopting, error, loadFailed } = props
  const cards = deck.cards
  const count = cards.length
  const perRow = gachaPerRow(count)
  const rows = gachaRows(count)
  const canConfirm = canConfirmGacha({ selection, cards, loadFailed, adopting })
  const note = phase === 'ready'
    ? gachaSelectionNote(selection, cards)
    : phase === 'loading' ? '正在预加载这批图…' : '正在揭晓…'
  const tintList = cards.map(card => props.tints[card.key] ?? FALLBACK_TINT)
  const burstTint = averageTint(tintList)
  const rowsOfCards = Array.from({ length: rows }, (_, row) => cards.slice(row * perRow, (row + 1) * perRow))
  // 判档开关关着时，服务端即便已经存过结果也不摆出来：卡面保持「未判档」。
  const showQuality = props.showQuality !== false
  const deckQualities = showQuality ? cards.map(card => card.quality) : []
  const omen = qualityOmen(deckQualities)
  // 与 Desktop 一致：判过档按最高档决定爆发光点数；没判过用统一常数（比白档多一个）。
  const sparkCount = omen
    ? qualitySparks({ status: 'graded', tier: omen, outputIndex: 0 })
    : UNGRADED_SPARKS
  // 与 Desktop 一致：只要有判出来的档位（含本地坏图白档）就把这句说明摆出来。
  const gradedAny = deckQualities.some(qualityIsGraded)

  const deckStyle: StyleVars = {
    '--gacha-w': `${gachaCardWidth(count)}px`,
    '--gacha-cols': Math.max(1, perRow),
    '--gacha-rows': Math.max(1, rows)
  }

  return (
    <div className={`df-gacha is-${phase}`} data-phase={phase} role="dialog" aria-modal="true" aria-label="出图开奖">
      <div className="df-gacha-aurora" aria-hidden="true" />
      <div className="df-gacha-spot" aria-hidden="true" />

      <div className="df-gacha-head">
        <div className="df-gacha-title">{props.nodeTitle}</div>
        <div className="df-gacha-sub">{gachaStatusLine(progress)}</div>
        {/* 档位是模型给的判断，这句话要写在画面上看得见的地方。 */}
        {gradedAny && <div className="df-gacha-disclaimer">{QUALITY_DISCLAIMER}</div>}
        {props.qualityNote && <div className="df-gacha-quality-note">{props.qualityNote}</div>}
        {deck.failed.map(item => (
          <div key={`failed-${item.number}`} className="df-gacha-fail" title={item.reason}>
            第 {item.number} 张 · {item.label}：{item.reason || '服务端没有给出原因'}
          </div>
        ))}
        {deck.cancelled.length > 0 && (
          <div className="df-gacha-fail is-muted">第 {deck.cancelled.join('、')} 张 · 已取消（没有参与开奖）</div>
        )}
        {progress.succeeded > deck.cards.length && (
          <div className="df-gacha-fail is-muted">已采用的图不再参与揭晓。</div>
        )}
      </div>

      {phase === 'loading' ? (
        <div className="df-gacha-stage">
          <div className="df-gacha-state">
            <span className="df-gacha-spinner" aria-hidden="true" />
            <span>正在预加载这批图（{props.loadedCount}/{count}）…</span>
            <span className="df-gacha-state-detail">加载完成之后才开始揭晓，避免翻到一半才看见白卡。</span>
          </div>
        </div>
      ) : phase === 'loadError' ? (
        <div className="df-gacha-stage">
          <div className="df-gacha-state">
            <span className="df-gacha-state-title">这批图没有加载出来</span>
            <span className="df-gacha-state-detail">
              已尝试 {count} 张，没有任何一张能读取。可以重新加载，或先关闭（本批候选会保留）。
            </span>
            <div className="df-gacha-state-actions">
              <button type="button" className="df-gacha-button" onClick={props.onRetry}>重新加载</button>
              <button type="button" className="df-gacha-button is-primary" onClick={props.onClose}>先不开</button>
            </div>
          </div>
        </div>
      ) : (
        <>
          <div className="df-gacha-stage">
            <div className="df-gacha-whiteout" aria-hidden="true" />
            <div className="df-gacha-pillar" aria-hidden="true" style={{ background: radial(burstTint, 0.55) }} />
            <div className="df-gacha-bloom" aria-hidden="true" />
            <div className="df-gacha-sparks" aria-hidden="true">
              {Array.from({ length: sparkCount }, (_, index) => (
                <span key={index} style={{
                  '--spark-i': index,
                  '--spark-x': `${(index - (sparkCount - 1) / 2) * 26}px`
                } as StyleVars} />
              ))}
            </div>

            <div className={`df-gacha-deck${selection !== null ? ' has-selection' : ''}`} style={deckStyle}>
              {rowsOfCards.map((row, rowIndex) => (
                <div className="df-gacha-row" key={rowIndex}>
                  {row.map((card, columnIndex) => {
                    const index = rowIndex * perRow + columnIndex
                    const offset = gachaOffset(count, index)
                    const tint = props.tints[card.key] ?? FALLBACK_TINT
                    const broken = loadFailed.has(card.key)
                    const selectable = phase === 'ready' && !broken
                    const quality = showQuality ? card.quality : null
                    const badge = qualityBadge(quality)
                    const cracked = qualityIsCracked(quality)
                    const gradeText = broken ? '加载失败' : badge.text
                    const cellStyle: StyleVars = {
                      '--dx': `${offset.x}px`,
                      '--dy': `${offset.y}px`,
                      '--fly-delay': `${index * GACHA_TIMING.cardFlyStaggerMs}ms`,
                      '--flip-delay': `${index * GACHA_TIMING.flipStaggerMs}ms`,
                      '--tint': tint
                    }
                    return (
                      <button
                        key={card.key}
                        type="button"
                        className={`df-gacha-cell${selection === index ? ' is-selected' : ''}${broken ? ' is-unavailable' : ''}${cracked ? ' is-cracked' : ''}`}
                        style={cellStyle}
                        data-tier={quality?.tier ?? undefined}
                        disabled={!selectable}
                        aria-pressed={selection === index}
                        aria-label={`第 ${card.number} 张${broken ? '：图片加载失败' : ''}${!broken && badge.graded ? `：${gradeText}` : ''}`}
                        onClick={() => { if (selectable) props.onSelect(index) }}
                      >
                        <span className="df-gacha-halo" style={{ background: radial(tint, 0.5) }} aria-hidden="true" />
                        <span className="df-gacha-halo-boost" style={{ background: radial(tint, 0.72) }} aria-hidden="true" />
                        <span className="df-gacha-card-persp">
                          <span className={`df-gacha-card-inner${phase === 'flips' ? ' is-flipping' : ''}${phase === 'ready' ? ' is-up' : ''}`}>
                            <span className="df-gacha-back">
                              <span className="df-gacha-back-line" aria-hidden="true" />
                              <span className="df-gacha-emblem">
                                <span className="df-gacha-emblem-ring">◈</span>
                                <span className="df-gacha-emblem-badge">AI</span>
                              </span>
                              <span className="df-gacha-corner" aria-hidden="true" />
                              <span className="df-gacha-chip is-top">{card.number}</span>
                            </span>
                            <span className="df-gacha-face">
                              {broken
                                ? <span className="df-gacha-broken">这张图读不出来</span>
                                : <img src={card.imageUrl} alt={`第 ${card.number} 张候选图`} draggable={false} />}
                              <span className="df-gacha-face-shade" aria-hidden="true" />
                              {/* 裂纹卡：踩中负面提示词的那张在这里裂开，压在图上、档位牌下。 */}
                              {cracked && (
                                <>
                                  <span className="df-gacha-crack-shade" aria-hidden="true" />
                                  <svg className="df-gacha-cracks" viewBox="0 0 100 150" preserveAspectRatio="none" aria-hidden="true">
                                    <polyline points="10,-2 21,28 12,55 25,82 19,120 26,152" />
                                    <polyline points="102,36 80,49 66,75 47,87 34,108" />
                                    <polyline points="58,152 63,117 50,97 42,70" />
                                    <polyline points="102,93 84,105 72,132" />
                                  </svg>
                                </>
                              )}
                              <span className="df-gacha-face-line" aria-hidden="true" />
                              <span className="df-gacha-corner" aria-hidden="true" />
                              <span className="df-gacha-chip is-bottom">第 {card.number} 张</span>
                              <span
                                className={`df-gacha-grade${badge.cracked ? ' is-cracked' : ''}`}
                                style={broken ? undefined : { color: badge.color, borderColor: badge.color }}
                              >
                                <span>{gradeText}</span>
                                {!broken && badge.cracked && (
                                  <span className="df-gacha-grade-hits">命中：{shortHits(quality?.negativeHits ?? [])}</span>
                                )}
                              </span>
                              <span className="df-gacha-flash" aria-hidden="true" />
                            </span>
                          </span>
                        </span>
                      </button>
                    )
                  })}
                </div>
              ))}
            </div>
            <div className="df-gacha-floor" aria-hidden="true" />
          </div>

          <div className="df-gacha-foot">
            <div className="df-gacha-hint" role={error.length > 0 ? 'alert' : undefined}>
              {error.length > 0 && <span className="df-gacha-hint-error">{error}</span>}
              <span>{note}</span>
            </div>
            <div className="df-gacha-actions">
              {phase !== 'ready' && (
                <button type="button" className="df-gacha-button" disabled={adopting} onClick={props.onSkip}>跳过动画</button>
              )}
              <button type="button" className="df-gacha-button" disabled={adopting} onClick={props.onClose}
                title="关闭：本批候选会保留，可以再次开奖">先不开</button>
              <button type="button" className="df-gacha-button is-primary" disabled={!canConfirm} onClick={() => {
                if (!canConfirm || adopting || selection === null) return
                props.onAdopt(cards[selection])
              }}>{adopting ? '正在采用…' : '用这一张'}</button>
            </div>
          </div>
        </>
      )}
    </div>
  )
}

/** 卡面小牌放不下长句：踩中的负面词截到 14 字，与 Desktop 的 Trim 一致。 */
function shortHits(hits: string[]): string {
  const text = hits.join('、')
  return text.length <= 14 ? text : `${text.slice(0, 14)}…`
}

/** 光晕用径向渐变，不做成一块实心色块。 */
function radial(color: string, alpha: number): string {
  return `radial-gradient(circle at 50% 50%, ${hexAlpha(color, alpha)} 0%, ${hexAlpha(color, alpha * 0.55)} 46%, ${hexAlpha(color, 0)} 72%)`
}

function hexAlpha(color: string, alpha: number): string {
  const match = /^#([0-9a-f]{6})$/i.exec(color.trim())
  if (!match) return `rgba(77, 155, 255, ${alpha})`
  const value = Number.parseInt(match[1], 16)
  const r = (value >> 16) & 0xff
  const g = (value >> 8) & 0xff
  const b = value & 0xff
  return `rgba(${r}, ${g}, ${b}, ${Math.max(0, Math.min(1, alpha)).toFixed(3)})`
}
