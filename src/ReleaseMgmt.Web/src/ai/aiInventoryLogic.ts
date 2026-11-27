import type { Tone } from '../ui'
import type { AiStatus } from '../mgmt/aiLogic.ts'

/** The pure parts of the AI inventory screen (REOS-184; D58): the shapes the API returns and the words the screen uses. Tested without a browser. */
export interface AiPerson { id: string; name: string }
export interface AiInputRef { label: string; class: string; sha256: string; chars: number }
export interface AiInventoryFeature {
  key: string; name: string; tier: string; model: string | null; enabled: boolean; blockedBecause: string | null
  calls: number; completed: number; accepted: number; edited: number; rejected: number; pending: number
  lastUsedAt: string | null; promptTemplateVersions: string[]; inputLabels: string[]
}
export interface AiInventoryModel { provider: string; model: string; servedVersions: string[]; calls: number; inputTokens: number; outputTokens: number; lastUsedAt: string | null; features: string[] }
export interface AiInventory {
  controls: AiStatus
  features: AiInventoryFeature[]
  models: AiInventoryModel[]
  data: { sent: string[]; neverSent: string[]; redacted: string[]; maxInputChars: number; hashesOnly: boolean }
  decisions: { total: number; pending: number; accepted: number; edited: number; rejected: number; refused: number }
  actionsConsidered: number
}
export interface AiActionSummary {
  id: string; feature: string; featureName: string; model: string | null; outcome: string; decision: string; inputTokens: number; outputTokens: number; edited: boolean
  subjectType: string | null; subjectId: string | null; requestedBy: AiPerson | null; requestedAt: string; decidedBy: AiPerson | null; decidedAt: string | null
  /** REOS-185: HardCap, ChatCap or RateLimit when the allowance refused the call (the outcome says only Blocked). */
  capReason?: string | null
}
export interface AiActionView {
  id: string; feature: string; featureName: string; provider: string; model: string | null; servedModel: string | null; promptTemplateVersion: string; promptHash: string | null
  inputs: AiInputRef[]; inputHash: string | null; outcome: string; detail: string | null; outputText: string | null; outputHash: string | null
  inputTokens: number; outputTokens: number; cacheReadTokens: number; cacheWriteTokens: number; attempts: number; redactions: number; batch: boolean
  subjectType: string | null; subjectId: string | null; releaseTrainId: string | null; requestedBy: AiPerson | null; requestedAt: string
  decision: string; finalText: string | null; finalHash: string | null; decisionNote: string | null; decidedBy: AiPerson | null; decidedAt: string | null; version: number
  integrity: { outputMatchesHash: boolean; finalMatchesHash: boolean; generatedAuditRow: boolean; decisionAuditRow: boolean; ok: boolean }
  capReason?: string | null
}

/** Status is never colour alone: every chip has these words. */
export const decisionWord = (d: string): string =>
  d === 'Pending' ? 'Awaiting decision' : d === 'Accepted' ? 'Accepted' : d === 'Edited' ? 'Edited and accepted' : d === 'Rejected' ? 'Rejected' : d === 'NotApplicable' ? 'No draft' : d
export const decisionTone = (d: string): Tone => (d === 'Pending' ? 'warning' : d === 'Accepted' || d === 'Edited' ? 'success' : d === 'Rejected' ? 'danger' : 'neutral')

const OUTCOMES: Record<string, string> = {
  Completed: 'Draft produced', ModuleOff: 'Refused: module off', KillSwitch: 'Refused: global kill switch', WorkspacePaused: 'Refused: workspace paused', OptedOut: 'Refused: workspace opted out',
  NotConfigured: 'Refused: no model configured', NoKey: 'Refused: no provider key', Blocked: 'Refused: data rule', TimedOut: 'Failed: timed out', ProviderError: 'Failed: provider error',
}
/** A Blocked call is a data rule, or (REOS-185) the allowance: the ledger's reason says which. */
const CAP_WORDS: Record<string, string> = { HardCap: 'Refused: allowance used up', ChatCap: 'Refused: chat is capped first', RateLimit: 'Refused: asked too often' }
export const outcomeWord = (o: string, capReason?: string | null): string => (capReason && CAP_WORDS[capReason]) || (OUTCOMES[o] ?? o)
export const outcomeTone = (o: string): Tone => (o === 'Completed' ? 'success' : o === 'TimedOut' || o === 'ProviderError' ? 'danger' : 'neutral')

export const featureState = (f: AiInventoryFeature): string => (f.enabled ? 'Available' : 'Not available')
export const featureReason = (f: AiInventoryFeature): string => (f.enabled ? 'Calls would be made now' : f.blockedBecause ?? 'Not available')

/** The one sentence that says what the kill switches add up to. */
export function killSwitchSummary(c: AiStatus): string {
  if (c.globalKillSwitch) return 'The global kill switch is on: no AI call is made anywhere.'
  if (!c.moduleEnabled) return 'The AI module is off in this workspace: no AI call is made.'
  if (c.workspacePaused) return 'AI is paused for this workspace: no AI call is made until it is resumed.'
  if (c.workspaceOptedOut) return 'This workspace opted out of AI: no AI call is made.'
  return c.canCall ? 'No kill switch is on: AI calls are allowed.' : `AI calls are not made: ${c.blockedBecause ?? 'not available'}.`
}

export const shortHash = (h: string | null): string => (h ? `${h.slice(0, 12)}…` : 'none')
export const tokenText = (i: number, o: number): string => `${i.toLocaleString('en-US')} in, ${o.toLocaleString('en-US')} out`
export const subjectText = (a: { subjectType: string | null; subjectId: string | null }): string => (a.subjectType && a.subjectId ? `${a.subjectType} ${a.subjectId}` : 'None')

/** May this person record the decision on a pending draft? The requester, or an administrator (the API checks again). */
export const canDecide = (a: { decision: string; outcome: string; requestedBy: AiPerson | null }, me: { id: string; roles: string[] }): boolean =>
  a.outcome === 'Completed' && a.decision === 'Pending' && (a.requestedBy?.id === me.id || me.roles.includes('RTE') || me.roles.includes('ReleaseManager'))
