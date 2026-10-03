import { useCallback, useEffect, useRef, useState } from 'react'
import { errorMessage, request } from '../api'
import { parseLeases, type Lease } from './locks'

/**
 * 编辑锁列表的轮询。
 *
 * 服务端已经有变更推送（SSE）了，收到 `edits.changed` 会立刻刷新一次。但这里**仍然保留轮询**：
 * 推送是加速，不是替代。少收到一条消息最多是晚 15 秒看到，而不是看到错的锁——
 * 后者会让人放心地去改别人正在改的东西。所以推送通了也不停表，两个轮子同时转。
 *
 * 15 秒足够让人看出「有人在编辑」，又不至于把请求刷成噪音。锁的 TTL 是两分钟，
 * 所以最坏情况下刚释放的锁会在界面上多留 15 秒：宁可晚一点消失，也不要谎称「现在没人编辑」。
 *
 * 锁这一路出错**不影响画布**：把错误单独拎出来显示，画布照旧可用。
 */
export function useLeases(enabled: boolean, intervalMs = 15000) {
  const [leases, setLeases] = useState<Lease[]>([])
  const [error, setError] = useState('')
  const [invalidCount, setInvalidCount] = useState(0)
  const generation = useRef(0)

  const refresh = useCallback(async () => {
    if (!enabled) return
    const run = ++generation.current
    try {
      const parsed = parseLeases(await request<unknown>('/api/web/edits'))
      if (run !== generation.current) return
      setLeases(parsed.leases)
      setInvalidCount(parsed.invalidCount)
      setError('')
    } catch (failure) {
      if (run !== generation.current) return
      setError(errorMessage(failure))
    }
  }, [enabled])

  useEffect(() => {
    if (!enabled) {
      setLeases([])
      setError('')
      setInvalidCount(0)
      return
    }
    void refresh()
    const timer = window.setInterval(() => void refresh(), intervalMs)
    return () => { generation.current++; window.clearInterval(timer) }
  }, [enabled, intervalMs, refresh])

  return { leases, error, invalidCount, refresh }
}
