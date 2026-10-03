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
  // 回调放 ref：它每次渲染都是新函数，进依赖数组会让「申请锁」变成每次渲染都跑一遍。
  const onChanged = useRef(options.onChanged)
  onChanged.current = options.onChanged

  useEffect(() => {
    if (!wanted || recordId.length === 0) return

    let cancelled = false
    let timer: number | undefined

    const acquire = async () => {
      try {
        const body = await request<{ lease?: unknown }>('/api/web/edits', {
          method: 'POST',
          body: JSON.stringify({ scope: 'node', targetId: recordId, client: 'web' })
        })
        const lease = parseLease(body?.lease)
        if (cancelled) return
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
        // 让画布上的锁列表尽快跟上：否则自己那张卡的徽标要等下一次轮询才出现。
        onChanged.current?.()
      } catch (failure) {
        if (cancelled) return
        leaseId.current = ''
        setHeld(false)
        // 409 是「别人在编辑」，不是错误：把持有者显示出来就够，不打错误提示。
        const holder = apiCode(failure) === 'EDIT_CONFLICT' ? holderOf(apiBody(failure)) : null
        setBlockedBy(holder)
        setError(holder ? '' : errorMessage(failure))
      }
    }

    const renew = async () => {
      const id = leaseId.current
      if (id.length === 0) {
        await acquire()
        return
      }
      try {
        const body = await request<{ lease?: unknown }>(`/api/web/edits/${id}`, { method: 'PUT' })
        const lease = parseLease(body?.lease)
        if (cancelled) return
        if (lease) {
          leaseId.current = lease.leaseId
          setHeld(true)
        }
      } catch {
        // 续期失败可能是锁已经被回收：清掉手里这个 id，下一次按新申请走。
        if (cancelled) return
        leaseId.current = ''
        setHeld(false)
        await acquire()
      }
    }

    // 每次进入都先申请一次：申请是幂等的（同一个人同一个目标回同一把锁），
    // 所以焦点来回切换不会造出一堆锁。
    void acquire()
    timer = window.setInterval(() => void renew(), HEARTBEAT_MS)

    return () => {
      cancelled = true
      if (timer !== undefined) window.clearInterval(timer)
      const stale = leaseId.current
      leaseId.current = ''
      setHeld(false)
      setBlockedBy(null)
      setError('')
      if (stale.length > 0) {
        // 还锁是「说一声就好」的事：失败了也不该在界面上留个错误——
        // 真没还成，服务端两分钟后会自己回收。
        void request(`/api/web/edits/${stale}`, { method: 'DELETE' }).catch(() => {})
        onChanged.current?.()
      }
    }
  }, [wanted, recordId])

  return { held, blockedBy, error }
}
