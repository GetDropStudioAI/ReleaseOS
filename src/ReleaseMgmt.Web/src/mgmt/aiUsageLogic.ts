import type { Tone } from '../ui'

/**
 * The pure parts of the AI allowance and usage panel (REOS-185; D57, D58): what GET /ai/usage and /ai/usage/ledger return and the words the panel uses. Money is whole micro-dollars on the wire and
 * credits (one cent each) on the screen. Status is always a word, never colour alone (CLAUDE.md rule 9). Tested without a browser.
 */
export interface AiFeatureUsage { feature: string; name: string; cappedFirst: boolean; calls: number; refused: number; costMicroUsd: number; inputTokens: number; outputTokens: number }
export interface AiUserUsage { userId: string; name: string; calls: number; costMicroUsd: number }
export interface AiCreditPack { id: string; microUsd: number; credits: number; source: string; reference: string | null; note: string | null; grantedByName: string | null; grantedAt: string }
export interface AiUsage {
  periodStart: string; periodEnd: string; resetsOn: string
  includedMicroUsd: number; packMicroUsd: number; totalMicroUsd: number; usedMicroUsd: number; reservedMicroUsd: number; remainingMicroUsd: number
  percentUsed: number; softCapPercent: number; chatSharePercent: number; chatUsedMicroUsd: number; chatLimitMicroUsd: number; chatAvailable: boolean
  userPerMinute: number; userPerHour: number; level: 'Ok' | 'Soft' | 'Hard'; message: string | null; microUsdPerCredit: number
  features: AiFeatureUsage[]; users: AiUserUsage[]; packs: AiCreditPack[]; opened: boolean
}
export interface AiLedgerEntry {
  id: string; feature: string; featureName: string; class: string; model: string | null; batch: boolean; state: string; refusalReason: string | null
  reservedMicroUsd: number; costMicroUsd: number; inputTokens: number; outputTokens: number; cacheReadTokens: number; cacheWriteTokens: number
  requestedBy: { id: string; name: string } | null; reservedAt: string; settledAt: string | null; aiActionId: string | null
}

/** Credits as people read them: up to two decimals, no trailing zeros. */
export const creditsText = (micro: number, perCredit: number): string => {
  const c = Math.round((micro / perCredit) * 100) / 100
  return c.toLocaleString('en-US', { maximumFractionDigits: 2 })
}
export const creditsUnit = (micro: number, perCredit: number): string => (Math.round((micro / perCredit) * 100) === 100 ? 'credit' : 'credits')
/** What it is in dollars, for the person who pays the provider: four decimals under a dollar, two above. */
export const usdText = (micro: number): string => {
  const usd = micro / 1_000_000
  return `$${usd.toLocaleString('en-US', { minimumFractionDigits: usd < 1 ? 4 : 2, maximumFractionDigits: usd < 1 ? 4 : 2 })}`
}

export const levelWord = (l: AiUsage['level']): string => (l === 'Hard' ? 'Allowance used up' : l === 'Soft' ? 'Chat paused' : 'Within allowance')
export const levelTone = (l: AiUsage['level']): Tone => (l === 'Hard' ? 'danger' : l === 'Soft' ? 'warning' : 'success')

export const percentText = (u: AiUsage): string => `${u.percentUsed}% used`

/** The one sentence under the meter: what the caps are, so nothing surprises anyone. */
export function capsText(u: AiUsage): string {
  const chat = u.chatSharePercent === 0 ? 'Chat is not included.' : `Chat is capped first: it stops at ${u.chatSharePercent}% of the allowance, or at the ${u.softCapPercent}% soft cap, whichever comes first.`
  const rate = u.userPerMinute > 0 || u.userPerHour > 0
    ? ` Each person may ask for AI help ${[u.userPerMinute > 0 ? `${u.userPerMinute} times a minute` : '', u.userPerHour > 0 ? `${u.userPerHour} times an hour` : ''].filter(Boolean).join(' and ')}.` : ''
  return `${chat} Drafts and summaries go on until the allowance is used up (100%).${rate}`
}

/** The notice above the meter when something is stopped; null while all is well. */
export function usageNotice(u: AiUsage): { tone: Tone; title: string; text: string } | null {
  if (u.level === 'Hard') return { tone: 'danger', title: 'The AI allowance is used up', text: u.message ?? '' }
  if (u.level === 'Soft') return { tone: 'warning', title: 'AI chat is paused', text: u.message ?? '' }
  return null
}

export const chatLine = (u: AiUsage): string =>
  u.chatSharePercent === 0 ? 'Not included' : u.chatAvailable ? `Available: ${creditsText(u.chatUsedMicroUsd, u.microUsdPerCredit)} of ${creditsText(u.chatLimitMicroUsd, u.microUsdPerCredit)} used` : 'Paused until the allowance renews'

export const featureKind = (f: AiFeatureUsage): string => (f.cappedFirst ? 'Chat: capped first' : 'Drafts and summaries')

const REFUSALS: Record<string, string> = { HardCap: 'Refused: allowance used up', ChatCap: 'Refused: chat is capped first', RateLimit: 'Refused: asked too often' }
export const refusalWord = (reason: string | null): string => (reason ? REFUSALS[reason] ?? `Refused: ${reason}` : 'Refused')
const STATES: Record<string, string> = { Reserved: 'In progress', Settled: 'Charged', Released: 'Not charged (call failed)' }
export const ledgerWord = (e: AiLedgerEntry): string => (e.state === 'Refused' ? refusalWord(e.refusalReason) : STATES[e.state] ?? e.state)
export const ledgerTone = (e: AiLedgerEntry): Tone => (e.state === 'Settled' ? 'success' : e.state === 'Refused' ? 'warning' : 'neutral')
export const ledgerWho = (e: AiLedgerEntry): string => e.requestedBy?.name ?? 'The system'
export const tokensText = (i: number, o: number): string => `${i.toLocaleString('en-US')} in, ${o.toLocaleString('en-US')} out`

/** The month the numbers belong to, e.g. "November 2026". */
export const periodText = (u: AiUsage): string => {
  const [y, m] = u.periodStart.split('-').map(Number)
  return new Date(Date.UTC(y, m - 1, 1)).toLocaleString('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' })
}
