/**
 * Pure parts of the Portfolio & calendar screen (REOS-96, REOS-134): UTC day arithmetic, the entries of a day, filters, and the scheduling conflicts.
 * Every day here is a UTC calendar day (D44: the screen is UTC; the person's own time is written beside each time).
 */
import { localClock } from './shell/clock.ts'
import { chipTone, type Tone } from './ui/tone.ts'
import type { ChainBrief } from './versions/versionsApi.ts'

export interface CalTrain { id: string; title: string; status: string; riskTier: string; targetDate: string; window: { startsAt: string; endsAt: string } | null; collisions?: { trainId: string; title: string; shared: string[] }[]; type?: { id: string; name: string } | null; betaNumber?: number | null; chain?: ChainBrief | null }
export interface CalGate { id: string; name: string; class: string; status: string; dueOn: string; trainId: string; trainTitle: string; trainStatus: string }
export interface CalFreeze { id: string; name: string; kind: 'Freeze' | 'Chill'; startsAt: string; endsAt: string; scope: string }
export interface CalMilestone { id: string; name: string; dueOn: string; done: boolean; trainId: string; trainTitle: string; trainStatus: string }
export interface CalendarPayload { from: string; to: string; trains: CalTrain[]; gates: CalGate[]; freezeWindows: CalFreeze[]; milestones?: CalMilestone[] }

// ---- date arithmetic on 'YYYY-MM-DD' strings (calendar days, no zone) ----
const pad = (n: number) => String(n).padStart(2, '0')
export const parseDay = (s: string) => { const [y, m, d] = s.split('-').map(Number); return Date.UTC(y, m - 1, d) }
const iso = (ms: number) => { const d = new Date(ms); return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}` }
export const addDays = (s: string, n: number) => iso(parseDay(s) + n * 86_400_000)
export function addMonths(s: string, n: number) {
  const d = new Date(parseDay(s)); const y = d.getUTCFullYear(), m = d.getUTCMonth() + n
  return iso(Date.UTC(y, m, Math.min(d.getUTCDate(), new Date(Date.UTC(y, m + 1, 0)).getUTCDate())))
}
const weekday = (s: string) => (new Date(parseDay(s)).getUTCDay() + 6) % 7   // Monday = 0
export const startOfWeek = (s: string) => addDays(s, -weekday(s))
export const utcDay = (t: string | number) => new Date(t).toISOString().slice(0, 10)
/** "14:00" in UTC. */
export const utcHM = (t: string | number) => new Date(t).toISOString().slice(11, 16)

export type Mode = 'week' | 'month'
/** The days shown for a focus date: the focus week, or whole weeks covering the month. */
export function visibleDays(focus: string, mode: Mode): string[] {
  let first: string, last: string
  if (mode === 'week') { first = startOfWeek(focus); last = addDays(first, 6) }
  else { const m1 = `${focus.slice(0, 7)}-01`; first = startOfWeek(m1); last = addDays(startOfWeek(addDays(addMonths(m1, 1), -1)), 6) }
  const out: string[] = []
  for (let d = first; d <= last; d = addDays(d, 1)) out.push(d)
  return out
}

/** "19–25 October 2026" for a week, "October 2026" for a month. */
export function rangeTitle(focus: string, mode: Mode): string {
  const long = (s: string, o: Intl.DateTimeFormatOptions) => new Date(parseDay(s)).toLocaleDateString('en-GB', { ...o, timeZone: 'UTC' })
  if (mode === 'month') return long(focus, { month: 'long', year: 'numeric' })
  const a = startOfWeek(focus), b = addDays(a, 6)
  const sameMonth = a.slice(0, 7) === b.slice(0, 7)
  return sameMonth ? `${Number(a.slice(8))}–${long(b, { day: 'numeric', month: 'long', year: 'numeric' })}`
    : `${long(a, { day: 'numeric', month: 'long' })} – ${long(b, { day: 'numeric', month: 'long', year: 'numeric' })}`
}

// ---- statuses ----
/** The status word the screen shows for a train: the lifecycle word itself, so a card's wording matches the Releases page. */
export const STATUSES = ['Planning', 'Gated', 'Executing', 'Complete', 'Aborted'] as const
export const RISKS: { value: string; label: string }[] = [
  { value: 'Low', label: 'Low' }, { value: 'Moderate', label: 'Moderate' }, { value: 'High', label: 'High' }, { value: 'VeryHigh', label: 'Very high' },
]
export const riskLabel = (r: string) => RISKS.find(x => x.value === r)?.label ?? r

export interface Filters { status: string; risk: string; type: string }   // 'all' or a value; type is a release type id (REOS-116)
export const NO_FILTER: Filters = { status: 'all', risk: 'all', type: 'all' }
export const filtered = (f: Filters) => f.status !== 'all' || f.risk !== 'all' || f.type !== 'all'
const keeps = (f: Filters, status: string, risk: string | undefined, type?: string) => (f.status === 'all' || f.status === status) && (f.risk === 'all' || (risk !== undefined && f.risk === risk)) && (f.type === 'all' || (type !== undefined && f.type === type))

/** The filters narrow trains, and the gates and milestones that belong to them, together; freezes are the workspace's and are never filtered. */
export function applyFilters(d: CalendarPayload, f: Filters): CalendarPayload {
  if (!filtered(f)) return d
  const risk = new Map(d.trains.map(t => [t.id, t.riskTier]))
  const type = new Map(d.trains.map(t => [t.id, t.type?.id]))
  const trainOk = (id: string, status: string) => keeps(f, status, risk.get(id), type.get(id))
  return {
    ...d,
    trains: d.trains.filter(t => keeps(f, t.status, t.riskTier, t.type?.id)),
    gates: d.gates.filter(g => trainOk(g.trainId, g.trainStatus)),
    milestones: (d.milestones ?? []).filter(m => trainOk(m.trainId, m.trainStatus)),
  }
}

// ---- conflicts ----
export interface Conflict { trainId: string; title: string; startsAt: string; kind: 'overlap' | 'freeze' | 'chill'; text: string }

/**
 * Scheduling conflicts of the trains in the payload, from their deployment windows: a window that overlaps another open train's and shares a CI or product (REOS-85, from
 * the server), and a window that overlaps a freeze (blocks without an override) or a chill (warns). Complete and Aborted trains have nothing left to conflict.
 */
export function conflictsOf(d: CalendarPayload): Conflict[] {
  const out: Conflict[] = []
  for (const t of d.trains) {
    if (!t.window || t.status === 'Complete' || t.status === 'Aborted') continue
    const s = Date.parse(t.window.startsAt), e = Date.parse(t.window.endsAt)
    for (const c of t.collisions ?? []) out.push({ trainId: t.id, title: t.title, startsAt: t.window.startsAt, kind: 'overlap', text: `${t.title} overlaps ${c.title} (${c.shared.join(', ')})` })
    for (const f of d.freezeWindows) {
      if (Date.parse(f.startsAt) < e && Date.parse(f.endsAt) > s)
        out.push({ trainId: t.id, title: t.title, startsAt: t.window.startsAt, kind: f.kind === 'Freeze' ? 'freeze' : 'chill', text: `${t.title} overlaps the ${f.kind.toLowerCase()} ${f.name} (${f.scope}, ${utcDay(f.startsAt)} to ${utcDay(Date.parse(f.endsAt) - 1000)})` })
    }
  }
  return out.sort((a, b) => a.startsAt.localeCompare(b.startsAt))
}

// ---- entries of a day ----
export interface Entry {
  key: string; kind: 'window' | 'target' | 'gate' | 'milestone'; trainId: string; gateId?: string; trainTitle: string; trainStatus: string; status: string
  /** The status word shown in the card's chip, and its tone (the word is always shown: colour only repeats it). */
  label: string; tone: Tone
  /** First line (the card's name) and the second (time range or what it is). */
  name: string; detail: string; title: string; conflict?: string
}

const GATE_WORD: Record<string, string> = { Pending: 'pending', InProgress: 'in progress', Certified: 'certified', Failed: 'failed', Waived: 'waived' }
const cap = (w: string) => w.charAt(0).toUpperCase() + w.slice(1)
const MILESTONE_TONE: Record<string, Tone> = { done: 'success', overdue: 'danger', open: 'neutral' }

/** Entries by UTC day. A window sits on the UTC day it opens; the target date, gate due dates and milestones are calendar dates. */
export function entriesByDay(d: CalendarPayload, today: string): Map<string, Entry[]> {
  const m = new Map<string, Entry[]>()
  const add = (day: string, e: Entry) => { const a = m.get(day); if (a) a.push(e); else m.set(day, [e]) }
  const clash = conflictsOf(d)
  for (const t of d.trains) {
    const word = t.status.toLowerCase()
    const why = clash.filter(c => c.trainId === t.id).map(c => c.text)
    if (t.window) {
      add(utcDay(t.window.startsAt), { key: `w:${t.id}`, kind: 'window', trainId: t.id, trainTitle: t.title, trainStatus: t.status, status: t.status, label: t.status, tone: chipTone(t.status), name: t.title,
        detail: `${utcHM(t.window.startsAt)}–${utcHM(t.window.endsAt)} UTC`, title: `${t.title}, deployment window ${utcHM(t.window.startsAt)} to ${utcHM(t.window.endsAt)} UTC, ${word}`, conflict: why[0] })
    }
    add(t.targetDate, { key: `t:${t.id}`, kind: 'target', trainId: t.id, trainTitle: t.title, trainStatus: t.status, status: t.status, label: t.status, tone: chipTone(t.status), name: t.title, detail: 'Target date', title: `${t.title}, target release date, ${word}` })
  }
  for (const g of d.gates)
    add(g.dueOn, { key: `g:${g.id}`, kind: 'gate', trainId: g.trainId, gateId: g.id, trainTitle: g.trainTitle, trainStatus: g.trainStatus, status: g.status, label: cap(GATE_WORD[g.status] ?? g.status.toLowerCase()), tone: chipTone(g.status), name: `${g.name} due`, detail: g.trainTitle, title: `${g.name} gate of ${g.trainTitle}, due, ${GATE_WORD[g.status] ?? g.status.toLowerCase()}` })
  // Q-0844: milestones are ◆ entries; the state word says done, open or overdue.
  for (const ms of d.milestones ?? []) {
    const state = ms.done ? 'done' : ms.dueOn < today ? 'overdue' : 'open'
    add(ms.dueOn, { key: `m:${ms.id}`, kind: 'milestone', trainId: ms.trainId, trainTitle: ms.trainTitle, trainStatus: ms.trainStatus, status: state, label: cap(state), tone: MILESTONE_TONE[state], name: `◆ ${ms.name}`, detail: ms.trainTitle, title: `Milestone ${ms.name} of ${ms.trainTitle}, ${state}` })
  }
  return m
}

/** Days each freeze or chill covers, as UTC days (the end instant is exclusive). */
export function freezeDays(d: CalendarPayload, lastDay: string): Map<string, CalFreeze[]> {
  const m = new Map<string, CalFreeze[]>()
  for (const f of d.freezeWindows) {
    const a = utcDay(f.startsAt), b = utcDay(Date.parse(f.endsAt) - 1000)
    for (let day = a; day <= b && day <= lastDay; day = addDays(day, 1)) { const arr = m.get(day); if (arr) arr.push(f); else m.set(day, [f]) }
  }
  return m
}

/** "2h window", "90m window" from a window's two instants. */
export function durationText(startsAt: string, endsAt: string): string {
  const min = Math.round((Date.parse(endsAt) - Date.parse(startsAt)) / 60000)
  if (min >= 60 && min % 60 === 0) return `${min / 60}h window`
  if (min > 60) return `${Math.floor(min / 60)}h ${min % 60}m window`
  return `${min}m window`
}

export interface ReleaseRow { id: string; title: string; status: string; riskTier?: string }
/** The week grid's rows: each release with an entry on a visible day (its own windows, target, gates or milestones), in order of its first one. */
export function releaseRows(d: CalendarPayload, byDay: Map<string, Entry[]>, days: string[]): ReleaseRow[] {
  const known = new Map(d.trains.map(t => [t.id, t]))
  const rows = new Map<string, ReleaseRow>()
  for (const day of days) for (const e of byDay.get(day) ?? []) {
    if (rows.has(e.trainId)) continue
    const t = known.get(e.trainId)
    rows.set(e.trainId, { id: e.trainId, title: e.trainTitle, status: t?.status ?? e.trainStatus, riskTier: t?.riskTier })
  }
  return [...rows.values()]
}

const MON = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
/** "23 Oct" for a calendar day. */
export const dayLabel = (day: string) => `${Number(day.slice(8))} ${MON[Number(day.slice(5, 7)) - 1]}`
/** An instant as UTC ("23 Oct 14:00 UTC") with the person's own time beside it (D44), null when they work in UTC. */
export function stamp(t: string | number, zone: string): { utc: string; local: string | null } {
  const ms = new Date(t).getTime()
  return { utc: `${dayLabel(utcDay(ms))} ${utcHM(ms)} UTC`, local: localClock(ms, zone) }
}
