import { useCallback, useEffect, useRef, useState } from 'react'
import { errorMessage, request } from '../api'
import { parsePresence, type PresencePerson } from './serverEvents'

/**
 * 在线名单的读取（`GET /api/web/presence`）。
 *
 * 收到 `presence.changed` 会立刻刷新一次；轮询是兜底——推送断了、或有人因 TTL 自然消失时，
 * 光靠推送会停在旧名单上。间隔取 15 秒，与编辑锁一致：**不比锁更密**，名单本来就是比锁更慢的东西。
 *
 * 在线≠拥有锁：这条通道出错只影响「谁在线」那一小块，画布与锁照旧可用，所以错误单独拎出来。
 */
export function usePresence(enabled: boolean, intervalMs = 15000) {
  const [people, setPeople] = useState<PresencePerson[]>([])
  const [error, setError] = useState('')
  const generation = useRef(0)

  const refresh = useCallback(async () => {
    if (!enabled) return
    const run = ++generation.current
    try {
      const snapshot = parsePresence(await request<unknown>('/api/web/presence'))
      if (run !== generation.current) return
      setPeople(snapshot.people)
      setError('')
    } catch (failure) {
      if (run !== generation.current) return
      setError(errorMessage(failure))
    }
  }, [enabled])

  useEffect(() => {
    if (!enabled) {
      setPeople([])
      setError('')
      return
    }
    void refresh()
    const timer = window.setInterval(() => void refresh(), intervalMs)
    return () => { generation.current++; window.clearInterval(timer) }
  }, [enabled, intervalMs, refresh])

  return { people, error, refresh }
}
