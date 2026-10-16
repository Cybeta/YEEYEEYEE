import { ChatPanel } from './ChatPanel'
import type { AgentCanvasPreview } from './agentPreview'
import type { ViewRecord } from './records'

export type NodeAgentRequest = { id: string; prompt: string; nodeId: string; chapterId?: string; baseRevision: number; title: string }

export type WebSkill = { id: string; name: string; capability: string; aspectRatios?: string[]; videoBackend?: string }

export type WebJob = {
  jobId: string
  targetNodeId?: string | null
  attachmentState?: string
  state: string
  progressPercent: number
  errorCode?: string | null
  errorMessage?: string
  attempt: number
  retryOfJobId?: string
  rootJobId: string
  canRetry: boolean
  outputs: { role: string; ref: string }[]
}

export type AgentPanelProps = {
  userId: string
  canChat: boolean
  chapterId?: string
  nodeId?: string
  baseRevision?: number
  canCommit: boolean
  /** 正式场景投影（变更前的那一份）。审批详情按 recordId 从这里取附件与引用的旧值。 */
  records: ViewRecord[]
  nodeAgentRequest?: NodeAgentRequest | null
  onNodeAgentConsumed?: (id: string) => void
  onPreviewChange?: (preview: AgentCanvasPreview | null) => void
  onChanged: () => Promise<void>
}

export function AgentPanel(props: AgentPanelProps) {
  return (
    <div className="df-agent-panel">
      <div className="df-panel-head"><span className="df-heading">Agent 协作</span></div>
      <div className="df-agent-chat-view"><ChatPanel key={props.userId} {...props} /></div>
    </div>
  )
}
