// REOS-97/126/127: the release detail page's logic, as pure functions of the API data so node --test can check it without a browser.
import type { Tone } from '../ui/tone.ts'
import { localClock } from '../shell/clock.ts'
import type { Condition, Decision, DecisionView, GateRow } from '../api.ts'

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const pad = (n: number) => String(n).padStart(2, '0')

/** D44: a time is UTC and labelled ("30 Oct 2026, 02:00 UTC"); the person's own time is beside it (null when they work in UTC). */
export function stamp(iso: string, zone: string): { utc: string; local: string | null } {
  const d = new Date(iso)
  return { utc: `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}, ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())} UTC`, local: localClock(d.getTime(), zone) }
}
/** A window as "30 Oct 2026, 02:00 – 04:00 UTC" (a second date when it ends on another UTC day) with the local span beside it. */
export function windowStamp(startsAt: string, endsAt: string, zone: string): { utc: string; local: string | null } {
  const a = new Date(startsAt), b = new Date(endsAt)
  const day = (d: Date) => `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}`
  const hm = (d: Date) => `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`
  const sameDay = day(a) === day(b)
  const utc = sameDay ? `${day(a)}, ${hm(a)} – ${hm(b)} UTC` : `${day(a)}, ${hm(a)} – ${day(b)}, ${hm(b)} UTC`
  const la = localClock(a.getTime(), zone), lb = localClock(b.getTime(), zone)
  return { utc, local: la && lb ? `${la} – ${lb}` : null }
}

// ---- quality gates -------------------------------------------------------------------------------------------------------------------------
export const isPassed = (status: string) => status === 'Certified' || status === 'Waived'

/** The words and tone of a gate's state in the design's vocabulary: Passed, Pending, plus the app's In progress, Failed and Waived. */
export function gateState(status: string): { word: string; tone: Tone } {
  switch (status) {
    case 'Certified': return { word: 'Passed', tone: 'success' }
    case 'Waived': return { word: 'Waived', tone: 'warning' }
    case 'Failed': return { word: 'Failed', tone: 'danger' }
    case 'InProgress': return { word: 'In progress', tone: 'info' }
    default: return { word: 'Pending', tone: 'neutral' }
  }
}

export interface GateSummary { total: number; passed: number; outstanding: number; ready: number; checksDone: number; checksTotal: number }
/** One summary feeds the score, the progress bar, the chip and the table's footer, so they can never disagree. `ready` is passed over total, 0 to 1. */
export function summarizeGates(gates: Pick<GateRow, 'status' | 'tasksDone' | 'tasksTotal'>[]): GateSummary {
  const passed = gates.filter(g => isPassed(g.status)).length
  return {
    total: gates.length, passed, outstanding: gates.length - passed, ready: gates.length === 0 ? 0 : passed / gates.length,
    checksDone: gates.reduce((n, g) => n + g.tasksDone, 0), checksTotal: gates.reduce((n, g) => n + g.tasksTotal, 0),
  }
}

/** What a gate that has not passed is waiting for, in words, with its owner: the open checks first, then the certification. */
export function waitingFor(g: Pick<GateRow, 'status' | 'tasksDone' | 'tasksTotal' | 'ownerName' | 'class'>): string {
  if (g.status === 'Certified') return 'Certified'
  if (g.status === 'Waived') return 'Waived by approval'
  const owner = g.ownerName ?? 'no owner set'
  if (g.status === 'Failed') return `Failed. ${owner} reopens it to continue`
  const open = g.tasksTotal - g.tasksDone
  if (open > 0) return `Waiting for ${open} of ${g.tasksTotal} ${open === 1 ? 'check' : 'checks'}; owner ${owner}`
  if (g.tasksTotal === 0 && g.class === 'Compliance') return `Needs at least one check before it can be certified; owner ${owner}`
  return `All checks done. Waiting for ${owner} to certify`
}

// ---- Go / no-go ---------------------------------------------------------------------------------------------------------------------------
export type GoKey = 'Go' | 'GoWithConditions' | 'NoGo' | 'None'
export interface GoCard { key: GoKey; label: string; headline: string; tone: Tone; decision: Decision | null; conditions: Condition[]; openConditions: Condition[] }

/** The card handles all four states with a label, a headline and a tone of its own: Go, Conditional go, No-go, Not yet decided. */
export function goCard(list: DecisionView[]): GoCard {
  const latest = list[0]
  if (!latest) return { key: 'None', label: 'Not yet decided', headline: 'No Go/No-Go decision has been recorded', tone: 'neutral', decision: null, conditions: [], openConditions: [] }
  const open = latest.conditions.filter(c => !c.closedAt)
  const base = { decision: latest.decision, conditions: latest.conditions, openConditions: open }
  switch (latest.decision.decision) {
    case 'Go': return { key: 'Go', label: 'Go', headline: 'Cleared to deploy', tone: 'success', ...base }
    case 'GoWithConditions': return { key: 'GoWithConditions', label: 'Conditional go', headline: open.length === 0 ? 'Cleared; every condition is closed' : `Cleared with ${open.length} open ${open.length === 1 ? 'condition' : 'conditions'}`, tone: 'warning', ...base }
    default: return { key: 'NoGo', label: 'No-go', headline: 'Deployment is on hold', tone: 'danger', ...base }
  }
}

// ---- approvals tab -------------------------------------------------------------------------------------------------------------------------
export interface OpenDecision { key: string; kind: 'Gate' | 'GoNoGo' | 'Condition'; title: string; detail: string; gateId?: string }

/** The decisions still open on this release: each gate not yet passed, the Go/No-Go when none is recorded, each open condition. The Approvals tab lists exactly these, and its count is their number. */
export function openDecisions(gates: GateRow[], goList: DecisionView[], status: string): OpenDecision[] {
  const rows: OpenDecision[] = gates.filter(g => !isPassed(g.status)).map(g => ({ key: `gate:${g.id}`, kind: 'Gate' as const, gateId: g.id, title: `Certify ${g.name}`, detail: waitingFor(g) }))
  const open = status === 'Planning' || status === 'Gated' || status === 'Executing'
  if (open && goList.length === 0) rows.push({ key: 'gonogo', kind: 'GoNoGo', title: 'Record the Go/No-Go decision', detail: 'Executing needs a Go.' })
  for (const c of goList[0]?.conditions ?? []) if (!c.closedAt) rows.push({ key: `cond:${c.id}`, kind: 'Condition', title: `Close condition: ${c.text}`, detail: 'Open Go condition' })
  return rows
}

// ---- tabs ------------------------------------------------------------------------------------------------------------------------------------
export const TABS = ['overview', 'services', 'dependencies', 'approvals', 'activity'] as const
export type TabId = typeof TABS[number]
export const TAB_LABEL: Record<TabId, string> = { overview: 'Overview & readiness', services: 'Services', dependencies: 'Dependencies', approvals: 'Approvals', activity: 'Activity' }

// ---- header ---------------------------------------------------------------------------------------------------------------------------------
/** The release's status chip: the app's own status word with the tone the design gives that stage. */
export function statusChip(status: string, closeCode: string | null): { word: string; tone: Tone } {
  switch (status) {
    case 'Planning': return { word: 'Planning', tone: 'neutral' }
    case 'Gated': return { word: 'Gated', tone: 'warning' }
    case 'Executing': return { word: 'Executing', tone: 'info' }
    case 'Complete': return { word: closeCode ? `Complete · ${closeCode.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase()}` : 'Complete', tone: closeCode === 'Unsuccessful' ? 'danger' : closeCode === 'SuccessfulWithIssues' ? 'warning' : 'success' }
    case 'Aborted': return { word: 'Aborted', tone: 'danger' }
    default: return { word: status, tone: 'neutral' }
  }
}
export function riskChip(tier: string): { word: string; tone: Tone } {
  switch (tier) {
    case 'Low': return { word: 'Low risk', tone: 'success' }
    case 'Moderate': return { word: 'Medium risk', tone: 'warning' }
    case 'High': return { word: 'High risk', tone: 'danger' }
    case 'VeryHigh': return { word: 'Very high risk', tone: 'danger' }
    default: return { word: `${tier} risk`, tone: 'neutral' }
  }
}

/** The design's fields the app does not record yet (REOS-107): shown as this text, never as an invented value. */
export const NOT_RECORDED = 'Not recorded'

export interface PropertyInput {
  changeTicket: string | null; backoutPlan: string | null; rollbackRehearsed: boolean; riskTier: string; cabDate: string | null; locked: boolean
}
/** Release properties: what the app holds is shown; target environments, release branch and on-call are not recorded. */
export function releaseProperties(p: PropertyInput): { label: string; value: string; mono?: boolean }[] {
  const needsRehearsal = p.riskTier === 'High' || p.riskTier === 'VeryHigh'
  const rollback = p.rollbackRehearsed ? 'Rehearsed' : p.backoutPlan ? 'Back-out plan written, not rehearsed' : needsRehearsal ? 'Not rehearsed (required before Executing)' : 'Not rehearsed'
  return [
    { label: 'Target environments', value: NOT_RECORDED },
    { label: 'Change record', value: p.changeTicket ?? 'No ticket linked', mono: !!p.changeTicket },
    { label: 'CAB date', value: p.cabDate ?? NOT_RECORDED, mono: !!p.cabDate },
    { label: 'Release branch', value: NOT_RECORDED },
    { label: 'Rollback plan', value: rollback },
    { label: 'On-call', value: NOT_RECORDED },
    { label: 'Service manifest', value: p.locked ? 'Locked' : 'Open for changes' },
  ]
}

/** The manifest is locked once deployment has started or the release has ended; the API refuses edits then (REOS-81). */
export const manifestLocked = (status: string) => status === 'Executing' || status === 'Complete' || status === 'Aborted'

export interface NoticeInput { status: string; nextStatus: string | null; ready: boolean | null; guardsMet: number; guardsTotal: number; blockers: number }
/** The operational notice above the quality gates: ready, blocked (with the count of guards and a Review blockers link), or the end state. */
export function releaseNotice(i: NoticeInput): { tone: Tone; title: string; body: string; review: boolean } {
  if (!i.nextStatus) return i.status === 'Aborted'
    ? { tone: 'danger', title: 'This release was aborted', body: 'Nothing more can be advanced.', review: false }
    : { tone: 'success', title: 'This release is complete', body: 'Close-out items are under Activity.', review: false }
  if (i.ready === null) return { tone: 'neutral', title: `Checking readiness for ${i.nextStatus}`, body: '', review: false }
  if (i.ready) return { tone: 'success', title: `Ready for ${i.nextStatus}`, body: `All ${i.guardsTotal} ${i.guardsTotal === 1 ? 'check' : 'checks'} for ${i.nextStatus} are met.`, review: false }
  return { tone: 'warning', title: `${i.blockers} ${i.blockers === 1 ? 'check is' : 'checks are'} blocking ${i.nextStatus}`, body: `${i.guardsMet} of ${i.guardsTotal} checks for ${i.nextStatus} are met.`, review: true }
}
