import type { ApprovalItem } from './approvalsApi.ts'

/** The pure parts of the Approvals screen (REOS-99): the queue's labels, due-time countdown, metric numbers and the decision note rules. */

export type Kind = ApprovalItem['kind']
export const itemKey = (i: Pick<ApprovalItem, 'kind' | 'id'>) => `${i.kind}:${i.id}`

/** Kinds that are exceptions to the normal path (the design's Exceptions tab): a waived gate and an overridden freeze. */
export const EXCEPTION_KINDS: Kind[] = ['ApproveWaiver', 'GrantOverride']

const PREFIX: Record<Kind, string> = { CertifyGate: 'CRT', ApproveWaiver: 'WVR', GrantOverride: 'OVR', RecordGoNoGo: 'GNG', CloseCondition: 'CND' }
/** The design's "APR-621": a short reference. Ids are UUIDv7 (the tail is the random part), so the last six characters tell two items apart. */
export function shortRef(i: Pick<ApprovalItem, 'kind' | 'id'>): string {
  const tail = i.id.split(':')[0].replace(/-/g, '').slice(-6).toUpperCase()
  return `${PREFIX[i.kind]}-${tail}`
}

/** An override item's id is `{windowId}:{trainId}:{requesterId}` (ApprovalQueueService). */
export function parseOverrideId(id: string): { windowId: string; trainId: string; requesterId: string } | null {
  const [windowId, trainId, requesterId, ...rest] = id.split(':')
  return windowId && trainId && requesterId && rest.length === 0 ? { windowId, trainId, requesterId } : null
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

function span(ms: number): string {
  const m = Math.floor(ms / 60000)
  if (m < 1) return 'under a minute'
  if (m < 60) return `${m}m`
  const h = Math.floor(m / 60)
  if (h < 24) return m % 60 === 0 ? `${h}h` : `${h}h ${m % 60}m`
  const d = Math.floor(h / 24)
  return `${d}d`
}

export interface Countdown { text: string; overdue: boolean; soon: boolean }

/**
 * "Due in 28m" for a timed item, "Due 28 Oct" for a dated one; once past, "Overdue by 5m" (timed) or "Overdue, was due 28 Oct" (dated).
 * `soon` is true within the hour: the screen then draws the text in the warning tone. Overdue is danger; both always say the word.
 */
export function countdown(i: Pick<ApprovalItem, 'dueOn' | 'dueAt' | 'overdue'>, nowMs: number): Countdown {
  if (i.dueAt) {
    const diff = Date.parse(i.dueAt) - nowMs
    if (diff <= 0) return { text: Math.abs(diff) < 60000 ? 'Due now' : `Overdue by ${span(-diff)}`, overdue: true, soon: false }
    return { text: `Due in ${span(diff)}`, overdue: false, soon: diff <= 3600000 }
  }
  if (i.dueOn) {
    const [y, m, d] = i.dueOn.split('-').map(Number)
    const day = `${d} ${MONTHS[m - 1]}${new Date(nowMs).getUTCFullYear() === y ? '' : ` ${y}`}`
    return i.overdue ? { text: `Overdue, was due ${day}`, overdue: true, soon: false } : { text: `Due ${day}`, overdue: false, soon: false }
  }
  return { text: 'No due date', overdue: false, soon: false }
}

/** "Due within an hour": timed items not yet due and due in 60 minutes or less. A decision that is already overdue is counted as overdue, not here. */
export function dueWithinHour(items: Pick<ApprovalItem, 'dueAt'>[], nowMs: number): number {
  return items.filter(i => i.dueAt !== null && Date.parse(i.dueAt) - nowMs > 0 && Date.parse(i.dueAt) - nowMs <= 3600000).length
}

export const overdueCount = (items: Pick<ApprovalItem, 'dueAt' | 'overdue'>[], nowMs: number) =>
  items.filter(i => (i.dueAt !== null ? Date.parse(i.dueAt) <= nowMs : i.overdue)).length

/** The same 20-character floor the services apply (WaiverService, FreezeService.MinReasonLength): the screen says so before the server has to. */
export const MIN_NOTE = 20

/**
 * The decision note rule per action. Only the API's own fields take a note: a Go/No-Go's notes (required for No-Go: a rejection needs its reason) and a
 * freeze override's reason. Gates, waivers and conditions take none, so the screen shows no note field for them rather than a note the server would drop.
 */
export function noteProblem(action: 'go' | 'nogo' | 'override', note: string): string | null {
  const t = note.trim()
  if (action === 'nogo' && t.length === 0) return 'A written reason is required to record No-Go.'
  if (action === 'override' && t.length < MIN_NOTE) return `An override needs a written reason of at least ${MIN_NOTE} characters (${t.length} so far).`
  return null
}
