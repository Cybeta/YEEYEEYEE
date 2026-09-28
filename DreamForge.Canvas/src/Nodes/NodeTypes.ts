export type DreamNodeKind = 'story-plan' | 'story-outline' | 'chapter' | 'scene-description' | 'image' | 'video' | 'text' | 'skill' | 'tool'
export interface DreamNodeData { kind: DreamNodeKind; title: string; assetId?: string; skillRef?: { kind: 'Skill'; targetId: string; versionConstraint: string }; jobId?: string; content?: string; status?: 'idle' | 'running' | 'succeeded' | 'failed' }
