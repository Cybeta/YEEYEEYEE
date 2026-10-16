import { useEffect, useRef, useState } from 'react'
import { apiBody, apiCode, errorMessage, request } from '../api'
import { holderOf, parseLease, type Lease } from './locks'

/**
 * 节点级编辑锁的持有者这一侧。
 *
 * 规则只有两条，但都值得写清楚：
 *
 * 1. **`wanted` 由界面给**（见 `shouldHoldNodeLease`）：真的打算改才为真。
 *    选中一个节点不算——点着看一圈就撒一地锁，是把别人挡在外面。
 * 2. **不需要了就立刻还**，不等它超时。超时是给「浏览器崩了/网断了」兜底的，
 *    不是正常路径：正常路径上让人多等两分钟，别人只会以为系统坏了。
 *
 * 续期失败（锁被回收、服务重启、管理员强制接管）时**不自欺**：清掉本地 leaseId
 * 并按一次新的申请走，而不是继续以为手里握着锁——那会导致保存时才发现锁不是自己的。
 */
const HEARTBEAT_MS = 20000

export type NodeLeaseState = {
  /** 自己正握着这个节点的锁。 */
  held: boolean
  /** 别人正握着（申请被拒时从 409 里取到的持有者）。 */
  blockedBy: Lease | null
  /** 与锁无关的失败（网络、服务端 5xx）。 */
  error: string
}

export function useNodeLease(options: { recordId: string; wanted: boolean; onChanged?: () => void }): NodeLeaseState {
  const { recordId, wanted } = options
  const [held, setHeld] = useState(false)
  const [blockedBy, setBlockedBy] = useState<Lease | null>(null)
  const [error, setError] = useState('')
  const leaseId = useRef('')
  // 同持有周期的申请幂等：恢复必须等旧请求及释放完成，不能让旧响应删掉恢复后的同一把锁。
  const pending = useRef<Promise<void>>(Promise.resolve())
  const onChanged = useRef(options.onChanged)
  onChanged.current = options.onChanged

  useEffect(() => {
    if (!wanted || recordId.length === 0) return

    const ownerId = crypto.randomUUID()
    let active = false
    let generation = 0
    let timer: number | undefined
    let busy = false
    const release = (id: string) => request(`/api/web/edits/${id}`, { method: 'DELETE', keepalive: true }).then(() => {}, () => {})
    const current = (run: number) => active && run === generation

    const acquire = async (run: number) => {
      try {
        const body = await request<{ lease?: unknown }>('/api/web/edits', {
          method: 'POST',
          body: JSON.stringify({ scope: 'node', targetId: recordId, client: 'web', ownerId })
        })
        const lease = parseLease(body?.lease)
        if (!current(run)) {
          if (lease) await release(lease.leaseId)
          return
        }
        if (!lease) {
          leaseId.current = ''
          setHeld(false)
          setError('申请锁的响应看不懂，暂不认为自己握着锁')
          return
        }
        leaseId.current = lease.leaseId
        setHeld(true)
        setBlockedBy(null)
        setError('')
        onChanged.current?.()
      } catch (failure) {
        if (!current(run)) return
        leaseId.current = ''
        setHeld(false)
        const holder = apiCode(failure) === 'EDIT_CONFLICT' ? holderOf(apiBody(failure)) : null
        setBlockedBy(holder)
        setError(holder ? '' : errorMessage(failure))
      }
    }

    const renew = async (run: number) => {
      const id = leaseId.current
      if (id.length === 0) {
        await acquire(run)
        return
      }
      try {
        const body = await request<{ lease?: unknown }>(`/api/web/edits/${id}`, { method: 'PUT' })
        const lease = parseLease(body?.lease)
        if (!current(run)) return
        if (lease) {
          leaseId.current = lease.leaseId
          setHeld(true)
        }
      } catch {
        if (!current(run)) return
        leaseId.current = ''
        setHeld(false)
        await acquire(run)
      }
    }

    const tick = () => {
      if (!active || busy) return
      busy = true
      const run = generation
      pending.current = pending.current.then(async () => {
        if (current(run)) await renew(run)
      }).finally(() => { if (current(run)) busy = false })
    }
    const start = () => {
      if (active) return
      active = true
      generation++
      busy = false
      tick()
      timer = window.setInterval(tick, HEARTBEAT_MS)
    }
    const stop = () => {
      active = false
      generation++
      if (timer !== undefined) window.clearInterval(timer)
      timer = undefined
      const stale = leaseId.current
      leaseId.current = ''
      setHeld(false)
      setBlockedBy(null)
      setError('')
      if (stale.length > 0) {
        // pagehide 必须立即发送；后续申请仍等待这次释放和未完成的旧请求。
        const released = release(stale)
        pending.current = Promise.all([pending.current, released]).then(() => {})
        onChanged.current?.()
      }
    }
    const onPageShow = (event: PageTransitionEvent) => { if (event.persisted) start() }

    start()
    window.addEventListener('pagehide', stop)
    window.addEventListener('pageshow', onPageShow)
    return () => {
      window.removeEventListener('pagehide', stop)
      window.removeEventListener('pageshow', onPageShow)
      stop()
    }
  }, [wanted, recordId])

  return { held, blockedBy, error }
}
