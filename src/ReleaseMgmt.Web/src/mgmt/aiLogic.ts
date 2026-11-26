/**
 * The pure parts of the AI settings panel (REOS-183; D58): the status GET /ai/status returns, the words the panel and its dialogs use, and which controls are offered.
 * The key is never part of the status: only whether one is present. Controls are buttons with confirmation dialogs, never toggle switches (CLAUDE.md rule 9). Tested without a browser.
 */
export interface AiFeatureRoute { key: string; name: string; tier: string; model: string | null }
export interface AiStatus {
  moduleEnabled: boolean; moduleEntitled: boolean; provider: string; keyPresent: boolean; keyRequired: boolean
  globalKillSwitch: boolean; workspacePaused: boolean; workspaceOptedOut: boolean
  models: { small: string | null; drafting: string | null; draftingFallsBackToSmall: boolean }
  features: AiFeatureRoute[]; timeoutSeconds: number; maxRetries: number; canCall: boolean; blockedBecause: string | null
}

export type AiAction = 'pause' | 'resume' | 'opt-out' | 'opt-in'

export const moduleLabel = (s: AiStatus): string => (s.moduleEnabled ? 'Enabled' : 'Not enabled')
export const keyLabel = (s: AiStatus): string => (!s.keyRequired ? 'Not needed (test provider)' : s.keyPresent ? 'Stored' : 'Not set')
export const globalLabel = (s: AiStatus): string => (s.globalKillSwitch ? 'On: no AI call is made anywhere' : 'Off')
export const pausedLabel = (s: AiStatus): string => (s.workspacePaused ? 'Paused' : 'Running')
export const optOutLabel = (s: AiStatus): string => (s.workspaceOptedOut ? 'Opted out' : 'Included')
export const draftingLabel = (s: AiStatus): string => (s.models.drafting ? s.models.drafting : s.models.small ? `${s.models.small} (same as small)` : 'Not configured')
export const smallLabel = (s: AiStatus): string => s.models.small ?? 'Not configured'

/** The one line that says whether a call would be made now, and if not why. */
export const callLabel = (s: AiStatus): string => (s.canCall ? 'AI calls are allowed' : `AI calls are not made: ${s.blockedBecause ?? 'not available'}`)

/** The pause button flips with the state; the same for opt-out. */
export const pauseAction = (s: AiStatus): AiAction => (s.workspacePaused ? 'resume' : 'pause')
export const optOutAction = (s: AiStatus): AiAction => (s.workspaceOptedOut ? 'opt-in' : 'opt-out')

export const actionLabel: Record<AiAction, string> = {
  pause: 'Pause AI for this workspace',
  resume: 'Resume AI for this workspace',
  'opt-out': 'Opt this workspace out of AI',
  'opt-in': 'Opt this workspace back in to AI',
}
export const actionDone: Record<AiAction, string> = {
  pause: 'AI paused for this workspace', resume: 'AI resumed for this workspace', 'opt-out': 'Workspace opted out of AI', 'opt-in': 'Workspace opted back in to AI',
}
export const actionIntro: Record<AiAction, string> = {
  pause: 'Pausing AI stops every AI call from this workspace straight away. It is the workspace kill switch.',
  resume: 'Resuming AI allows AI calls from this workspace again, as long as the module is enabled and no other switch is on.',
  'opt-out': 'Opting out means no data from this workspace is ever sent to a model, for any feature.',
  'opt-in': 'Opting back in allows AI features to use this workspace’s release data again, as long as the module is enabled.',
}
export const ACTION_NOTES = ['AI is advisory only. It never approves or changes a status (D58).', 'Ticket summaries and customer data are never sent to a model.']
export const actionIsRestrictive = (a: AiAction): boolean => a === 'pause' || a === 'opt-out'
