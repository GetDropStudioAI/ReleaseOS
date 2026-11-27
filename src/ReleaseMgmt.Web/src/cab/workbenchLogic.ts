import type { Me } from '../api.ts'
import type { Tone } from '../ui/tone.ts'
import type { ChangeRow, DecisionRow } from './cabApi.ts'
import { canDecide, canPlan, selfApprovalReason } from './cabLogic.ts'
import type { AgendaItem, Attendee, Meeting } from './workbenchApi.ts'

/**
 * The pure parts of the CAB workbench (REOS-179; D56, D63): who may run the meeting, which agenda items are still to decide, the conflict, freeze and blackout flags next to each item, the
 * per-item timer, and the checks on a decision before it is sent. Tested without a browser. Status is always a word; tone only repeats it.
 */
export const MEETING_STATUS_WORD: Record<string, string> = { Planned: 'Planned', InProgress: 'In progress', Completed: 'Complete', Cancelled: 'Cancelled' }
export const meetingStatusWord = (s: string) => MEETING_STATUS_WORD[s] ?? s
export function meetingTone(status: string): Tone { return status === 'InProgress' ? 'info' : status === 'Completed' ? 'success' : status === 'Planned' ? 'warning' : 'neutral' }
export const isOpen = (m: Pick<Meeting, 'status'>) => m.status === 'Planned' || m.status === 'InProgress'
export const isHeld = (m: Pick<Meeting, 'status'>) => m.status === 'InProgress'

/** Running the meeting (start, complete, reorder, roll) is the Plan policy; deciding is CabDecide. Neither works on a finished meeting. */
export const canRun = (me: Pick<Me, 'roles'>, m: Pick<Meeting, 'status'>) => canPlan(me) && isOpen(m)
export const canDecideHere = (me: Pick<Me, 'roles'>, m: Pick<Meeting, 'status'>) => canDecide(me) && isHeld(m)

export const ATTENDANCE_WORD: Record<string, string> = { Invited: 'Invited, no answer yet', Attended: 'Attended', Absent: 'Absent' }
export function attendanceTone(a: string): Tone { return a === 'Attended' ? 'success' : a === 'Absent' ? 'danger' : 'neutral' }
export const roleWord = (a: Pick<Attendee, 'isChair' | 'attendeeRole'>) => (a.isChair ? 'Chair' : a.attendeeRole)

/** Chair first, then by name: the order the API already uses, kept stable when a row changes. */
export const connectedCount = (people: Attendee[], connected: ReadonlySet<string>) => people.filter(a => connected.has(a.person.id)).length

/** What this meeting decided about one change: the latest decision recorded here, else the outcome if it was decided elsewhere, else nothing yet. */
export type Outcome = 'Approved' | 'Rejected' | 'Deferred' | null
export function outcomeOf(c: Pick<ChangeRow, 'status' | 'decisions'>, meetingId: string): Outcome {
  const here = [...c.decisions].filter(d => d.meetingId === meetingId && !d.retroactive).sort((a, b) => a.decidedAt.localeCompare(b.decidedAt)).at(-1)
  if (here) return here.decision as Outcome
  return c.status === 'Approved' ? 'Approved' : c.status === 'Rejected' ? 'Rejected' : null
}
/** Deferred stays on the table: the CAB can come back to it in the same meeting. */
export const isPending = (o: Outcome) => o === null || o === 'Deferred'

export interface Row { item: AgendaItem; change: ChangeRow | undefined; outcome: Outcome }
export function rowsOf(m: Pick<Meeting, 'agenda' | 'id'>, changes: ReadonlyMap<string, ChangeRow>): Row[] {
  return [...m.agenda].sort((a, b) => a.sequenceOrder - b.sequenceOrder).map(item => {
    const change = changes.get(item.changeRequestId)
    return { item, change, outcome: change ? outcomeOf(change, m.id) : null }
  })
}
export const pendingRows = (rows: Row[]) => rows.filter(r => isPending(r.outcome))
export const decidedRows = (rows: Row[]) => rows.filter(r => !isPending(r.outcome))

/** The item shown when none is picked: the first one still to decide, else the first. */
export const defaultSelection = (rows: Row[]): string | null => (pendingRows(rows)[0] ?? rows[0])?.item.id ?? null

export function summaryLine(rows: Row[]): string {
  const left = pendingRows(rows).length
  return `${rows.length} change${rows.length === 1 ? '' : 's'} on the agenda, ${left} still to decide`
}

/** The position (1-based) to send for a move. Skip puts the item last; "take next" puts it first among those still to decide. */
export const positionAfterSkip = (rows: Row[]) => rows.length
export function positionForTakeNext(rows: Row[], itemId: string): number {
  const first = pendingRows(rows)[0]
  if (!first || first.item.id === itemId) return first ? first.item.sequenceOrder : 1
  const own = rows.find(r => r.item.id === itemId)
  return own && own.item.sequenceOrder < first.item.sequenceOrder ? first.item.sequenceOrder - 1 : first.item.sequenceOrder
}

// ---- flags -------------------------------------------------------------------------------------------------------------------------------

export interface Flag { kind: 'Conflict' | 'Freeze' | 'Blackout'; label: string; detail: string; tone: Tone }

const overlaps = (a: ChangeRow, b: ChangeRow) =>
  !!(a.plannedStartAt && a.plannedEndAt && b.plannedStartAt && b.plannedEndAt && a.plannedStartAt < b.plannedEndAt && b.plannedStartAt < a.plannedEndAt)

/**
 * The flags next to one agenda item, from what the API already knows: the change's own freeze rule (inside a freeze window, inside a chill window, near one) and its planned window against
 * the other changes on this agenda. A freeze window is the app's blackout, so it is named both ways. No flag means nothing was found, and the screen says so in words.
 */
export function flagsFor(change: ChangeRow | undefined, others: ChangeRow[]): Flag[] {
  if (!change) return []
  const out: Flag[] = []
  const f = change.risk?.factors.find(x => x.rule === 'FreezeWindow' && x.applied)
  if (f) {
    if (f.points >= 3) out.push({ kind: 'Blackout', label: 'Inside a freeze (blackout)', detail: f.why, tone: 'danger' })
    else if (f.points === 2) out.push({ kind: 'Freeze', label: 'Inside a chill window', detail: f.why, tone: 'warning' })
    else out.push({ kind: 'Freeze', label: 'Near a freeze or chill window', detail: f.why, tone: 'warning' })
  }
  const clash = others.filter(o => o.id !== change.id && overlaps(change, o))
  if (clash.length) out.push({ kind: 'Conflict', label: `Overlaps ${clash.map(c => c.key).join(', ')}`, detail: `Planned at the same time as ${clash.map(c => `${c.key} (${c.title})`).join(', ')}.`, tone: 'warning' })
  return out
}
export const NO_FLAGS = 'No conflict, freeze or blackout found.'

// ---- decisions ---------------------------------------------------------------------------------------------------------------------------

export type Verdict = 'Approved' | 'Rejected' | 'Deferred'
export const VERDICT_LABEL: Record<Verdict, string> = { Approved: 'Approve', Rejected: 'Reject', Deferred: 'Defer' }

/** Why this person cannot record this verdict yet, in words, or null. Approve and Reject need a comment (the meeting record says why); deferring does not. The service has the last word. */
export function decisionBlock(verdict: Verdict, comment: string, change: Pick<ChangeRow, 'requestedBy'>, me: Pick<Me, 'id'>): string | null {
  if (verdict !== 'Deferred') {
    const self = selfApprovalReason(change, me)
    if (self) return self
    if (!comment.trim()) return `${verdict === 'Approved' ? 'An approval' : 'A rejection'} needs a comment.`
  }
  return null
}

export function decisionLine(d: Pick<DecisionRow, 'decision'>, key: string): string {
  return `${d.decision} ${key}`
}

/** The record of the meeting: every decision with the change it was about, oldest first (append-only, so the order never changes). */
export function recordOf(rows: Row[], meetingId: string): { change: ChangeRow; decision: DecisionRow }[] {
  return rows.flatMap(r => (r.change ? r.change.decisions.filter(d => d.meetingId === meetingId).map(decision => ({ change: r.change!, decision })) : []))
    .sort((a, b) => a.decision.decidedAt.localeCompare(b.decision.decidedAt) || a.decision.id.localeCompare(b.decision.id))
}

// ---- completion --------------------------------------------------------------------------------------------------------------------------

/** What the Complete dialog says: how many items have no decision, so ending the meeting is a choice made with that in view. */
export function completeWarning(rows: Row[]): string {
  const left = pendingRows(rows).length
  return left === 0 ? 'Every change on the agenda has a decision.' : `${left} change${left === 1 ? ' has' : 's have'} no decision yet and will stay as they are (Submitted).`
}

// ---- timer -------------------------------------------------------------------------------------------------------------------------------

/** "07:05" or "1:02:03": time spent on an item. Whole seconds, never negative. */
export function clock(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000)), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60
  const two = (n: number) => String(n).padStart(2, '0')
  return h > 0 ? `${h}:${two(m)}:${two(r)}` : `${two(m)}:${two(r)}`
}

export interface Timer { running: string | null; since: number; spent: Record<string, number> }
export const newTimer = (): Timer => ({ running: null, since: 0, spent: {} })
export const spentOn = (t: Timer, itemId: string, now: number) => (t.spent[itemId] ?? 0) + (t.running === itemId ? Math.max(0, now - t.since) : 0)
/** Starts the timer on an item; whichever item was running is paused first, so only one runs. */
export function startTimer(t: Timer, itemId: string, now: number): Timer {
  const p = pauseTimer(t, now)
  return { ...p, running: itemId, since: now }
}
export function pauseTimer(t: Timer, now: number): Timer {
  if (!t.running) return t
  return { running: null, since: 0, spent: { ...t.spent, [t.running]: spentOn(t, t.running, now) } }
}
export function resetTimer(t: Timer, itemId: string): Timer {
  const spent = { ...t.spent }; delete spent[itemId]
  return t.running === itemId ? { running: null, since: 0, spent } : { ...t, spent }
}
