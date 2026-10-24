import { get } from '../api.ts'
import { dueText } from './approvalsApi.ts'

/** GET /overview?from&to (REOS-108, B2): everything on the Organization overview in one read. Definitions: docs/QUESTIONS.md Q-108a. */
export interface Card { value: number | null; detail: string }
export interface Overview {
  from: string; to: string; asOf: string
  cards: { activeReleases: Card; runningNow: Card; needAttention: Card; deploymentSuccess: Card & { deltaPts: number | null } }
  attention: AttentionItem[]
  throughput: Day[]
  activity: ActivityRow[]
}
export interface AttentionItem { category: 'Approval' | 'FailedGate' | 'Conflict'; kind: string; title: string; detail: string; trainId: string; trainTitle: string; dueAt: string; entityId: string; actionBy: string | null }
export interface Day { date: string; successful: number; withIssues: number; unsuccessful: number }
export interface ActivityRow {
  trainId: string; title: string; status: string; displayStatus: string; closeCode: string | null; riskTier: string; targetReleaseDate: string
  windowStartsAt: string | null; windowEndsAt: string | null; endedAt: string | null; products: number; blockers: number
  chain?: import('../versions/versionsApi.ts').ChainBrief | null   // RT2 (D48)
}

export const RANGES = [{ value: '7', label: 'Last 7 days' }, { value: '14', label: 'Last 14 days' }, { value: '30', label: 'Last 30 days' }, { value: '90', label: 'Last 90 days' }]

const isoDay = (d: Date) => d.toISOString().slice(0, 10)

/** The `from` date (UTC, inclusive) of a range of `days` days ending today (UTC). The server takes `to` as today. */
export function rangeFrom(days: number, now = new Date()): string {
  const d = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()))
  d.setUTCDate(d.getUTCDate() - (days - 1))
  return isoDay(d)
}

export const getOverview = (days: number) => get<Overview>(`/api/v1/overview?from=${rangeFrom(days)}`)

/** Top of the y axis: the tallest day rounded up to an even number, at least 4, so a quiet fortnight still draws a readable scale. */
export function chartMax(days: Day[]): number {
  const tallest = Math.max(0, ...days.map(d => d.successful + d.withIssues + d.unsuccessful))
  return Math.max(4, Math.ceil(tallest / 2) * 2)
}

/** The sentence a screen reader gets for one day, and the chart's tooltip line. */
export function dayText(d: Day): string {
  const total = d.successful + d.withIssues + d.unsuccessful
  if (total === 0) return `${shortDay(d.date)}: no releases completed`
  const parts = [`${d.successful} successful`, d.withIssues > 0 && `${d.withIssues} with issues`, d.unsuccessful > 0 && `${d.unsuccessful} failed`].filter(Boolean)
  return `${shortDay(d.date)}: ${parts.join(', ')}`
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
export const shortDay = (iso: string) => { const [, m, d] = iso.split('-').map(Number); return `${d} ${MONTHS[m - 1]}` }

/** A card's detail colour: the success card is green when it improved and amber when it fell; the attention card is amber while anything needs attention. */
export function successTone(deltaPts: number | null): 'success' | 'warning' | 'neutral' { return deltaPts === null || deltaPts === 0 ? 'neutral' : deltaPts > 0 ? 'success' : 'warning' }
export const attentionTone = (n: number | null): 'warning' | 'neutral' => (n ?? 0) > 0 ? 'warning' : 'neutral'

/** The release row's window: UTC with the person's time beside it (D44), or the target date while no window is booked. */
export function windowText(r: ActivityRow) {
  if (r.windowStartsAt) return dueText({ dueAt: r.windowStartsAt, dueOn: null })
  return { utc: `Target ${dueText({ dueAt: null, dueOn: r.targetReleaseDate }).utc}`, local: null }
}
