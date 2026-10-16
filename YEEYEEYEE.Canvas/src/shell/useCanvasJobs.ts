import { useCallback, useEffect, useRef, useState } from 'react'
import { errorMessage, request } from '../api'
import { hasActiveJobs, hasPendingAttachments, isJobActive, parseJobs, type NodeJob } from './nodeMedia'

/**
 * 生成任务的轮询（`GET /api/web/jobs`）。
 *
 * 为什么需要单独取一次：节点的「生成中 / 失败」**不写在画布里**——服务端只在产物回挂成功时
 * 把节点的执行状态写成 Completed，跑到哪一步只有任务自己知道。任务与节点的关系由任务带着
 * （`targetNodeId`，发起那一刻固定），所以界面必须按任务列表去认，不能拿「当前选中的节点」去套。
 *
 * 轮询快慢跟着「还有没有事在等」走：闲时 10 秒一次（只为发现别处发起的任务），
 * 有任务在跑、或**有任务已结束但产物还在回挂路上**时 1.5 秒——任务通常十几秒就结束，
 * 而「成功」到「节点上真的多了一件产物」之间还有一小段空档，慢表会让人以为界面卡住了。
 * SSE（`canvas.changed`，scope `job-output`）到达时调用方也会顺手 refresh 一次；
 * 这里**不再另起一套定时器**，就这一个轮询。
 *
 * 任务落终态、以及**已结束任务的产物刚回挂完成**（pending → attached / 给出失败原因）时
 * 各回调一次 `onSettled`：产物回挂是服务端在任务成功之后另做的一步，
 * 让调用方去把画布重新取一次（推送断开时这就是唯一的发现路径）。
 */
export function useCanvasJobs(enabled: boolean, onSettled?: () => void) {
  const [jobs, setJobs] = useState<NodeJob[]>([])
  const [error, setError] = useState('')
  const [polling, setPolling] = useState(false)
  const generation = useRef(0)
  /** 上一轮各任务的样子：用来认出「刚落下终态」与「附件刚回挂完成」这两件事。 */
  const previous = useRef(new Map<string, { state: string; attachmentState: string }>())
  /** 已经取过一轮列表了吗。第一轮是「刚载入」那一轮，调用方手上那份场景就是刚取的。 */
  const primed = useRef(false)
  const settled = useRef(onSettled)
  settled.current = onSettled

  const refresh = useCallback(async () => {
    if (!enabled) return
    const run = ++generation.current
    try {
      const parsed = parseJobs(await request<unknown>('/api/web/jobs'))
      if (run !== generation.current) return
      setJobs(parsed.jobs)
      setError('')
      const before = previous.current
      const firstRound = !primed.current
      primed.current = true
      // 值得让调用方去重取画布的两件事：任务刚落终态；已结束任务的产物刚回挂完成。
      // 后者最容易漏——任务早就 Succeeded 了，附件是稍后才写进节点的。
      const changed = parsed.jobs.some((job) => {
        const was = before.get(job.jobId)
        // 第一次看到这个任务、而它已经回挂完成：这一件产物可能是我手上这份场景里没有的
        // （任务在别处发起、推送又断了）。第一轮不算——那一轮调用方刚载入过场景，要什么都已经在手里。
        if (!was) return !firstRound && job.state === 'Succeeded' && job.attachmentState?.trim() === 'attached'
        return (isJobActive(was.state) && !isJobActive(job.state))
          || (was.attachmentState === 'pending' && job.attachmentState?.trim() !== 'pending')
      })
      previous.current = new Map(parsed.jobs.map((job) =>
        [job.jobId, { state: job.state, attachmentState: job.attachmentState?.trim() ?? '' }]))
      setPolling(hasActiveJobs(parsed.jobs) || hasPendingAttachments(parsed.jobs))
      if (changed) settled.current?.()
    } catch (failure) {
      if (run !== generation.current) return
      setError(errorMessage(failure))
    }
  }, [enabled])

  useEffect(() => {
    if (!enabled) {
      setJobs([])
      setError('')
      setPolling(false)
      previous.current = new Map()
      // 重新开启时是「刚载入」那一轮：上一次的列表不算数，否则会把旧任务当成「刚出现」。
      primed.current = false
      return
    }
    void refresh()
    const timer = window.setInterval(() => void refresh(), polling ? 1500 : 10000)
    return () => { generation.current++; window.clearInterval(timer) }
  }, [enabled, polling, refresh])

  return { jobs, error, hasActive: hasActiveJobs(jobs), refresh }
}
