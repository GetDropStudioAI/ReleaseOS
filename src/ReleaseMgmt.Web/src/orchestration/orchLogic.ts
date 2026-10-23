import type { Deployment } from '../deploy/deployApi.ts'
import type { RunStepRow } from '../api.ts'
import type { RunStages, StageSummary } from './orchApi.ts'
import { chipTone, type Tone } from '../ui/tone.ts'

/** The pure parts of the Orchestration screen (REOS-98, REOS-128): stage cards, step states, durations and the bar geometry. Tested without a browser. */

const pad = (n: number) => String(n).padStart(2, '0')
/** "14:08", UTC; the card says UTC once. */
export const hm = (ms: number) => { const d = new Date(ms); return `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}` }
/** "14:08:12", UTC, for the step table (its heading says UTC). */
export const hms = (ms: number) => { const d = new Date(ms); return `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}` }

/** RUN-nnnn in the design. The model has no run number, so the key is the last six characters of the run's id (QUESTIONS Q-98a). */
export const runKey = (id: string) => `RUN-${id.replace(/[^0-9a-z]/gi, '').slice(-6).toUpperCase()}`

/** "32m 08s", or "1h 02m 08s" from an hour up; never negative. */
export function elapsed(totalSec: number): string {
  const s = Math.max(0, Math.trunc(totalSec)), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), ss = s % 60
  return h > 0 ? `${h}h ${pad(m)}m ${pad(ss)}s` : `${m}m ${pad(ss)}s`
}
/** The step table's duration: "01m 08s". */
export function duration(totalSec: number): string {
  const s = Math.max(0, Math.trunc(totalSec)), m = Math.floor(s / 60)
  return `${pad(m)}m ${pad(s % 60)}s`
}

export const SECTION_ORDER = ['PreCheck', 'Deploy', 'Verify', 'Hypercare', 'Rollback']
export const SECTION_TITLE: Record<string, string> = { PreCheck: 'Pre-check', Deploy: 'Deploy', Verify: 'Verify', Hypercare: 'Hypercare', Rollback: 'Rollback' }
export const sectionTitle = (s: string) => SECTION_TITLE[s] ?? s

export type StageWord = 'Successful' | 'Running' | 'Failed' | 'Blocked' | 'Planned' | 'Skipped'
export interface Bullet { text: string; mark: 'done' | 'running' | 'failed' | 'skipped' | 'waiting'; word: string }
export interface StageCard {
  section: string; title: string; word: StageWord; tone: Tone; counts: StageSummary; bullets: Bullet[]; summary: string; span: string; announce: string
}

const MARK_WORD: Record<Bullet['mark'], string> = { done: 'done', running: 'running', failed: 'failed', skipped: 'skipped', waiting: 'queued' }
const markOf = (status: string): Bullet['mark'] => (status === 'Done' ? 'done' : status === 'Running' ? 'running' : status === 'Failed' ? 'failed' : status === 'Skipped' ? 'skipped' : 'waiting')

/** Stages in the order a run reads (Rollback last), whatever order the server sent. A run with fewer or more sections than five just has fewer or more cards. */
export const orderedStages = (stages: StageSummary[]) =>
  [...stages].sort((a, b) => rank(a.section) - rank(b.section) || (a.plannedStartAt ?? '').localeCompare(b.plannedStartAt ?? ''))
const rank = (s: string) => { const i = SECTION_ORDER.indexOf(s); return i < 0 ? SECTION_ORDER.length - 1.5 : i }

/**
 * One card per runbook section (QUESTIONS Q-98a: the mapping draws a card per section; per-environment stages need D42 data the runbook does not carry).
 * A stage that has not started is Blocked while the run is paused or an earlier stage has a failed step (the steps after it cannot start); the Rollback
 * section is never Blocked, it waits for someone to call it. Once the run has ended, a stage that never started is Skipped.
 */
export function stageCards(run: Pick<RunStages, 'paused' | 'pauseReason' | 'endedAt' | 'stages'>, steps: RunStepRow[], nowMs: number): StageCard[] {
  const stages = orderedStages(run.stages)
  const failedBefore = (i: number) => stages.slice(0, i).some(s => s.section !== 'Rollback' && s.failed > 0)
  return stages.map((s, i) => {
    const mine = steps.filter(x => x.section === s.section).sort((a, b) => a.plannedStartAt.localeCompare(b.plannedStartAt))
    const failedStep = steps.find(x => x.status === 'Failed' && x.section !== 'Rollback' && stages.findIndex(t => t.section === x.section) < i)
    const held = s.status === 'Pending' && s.section !== 'Rollback' && !run.endedAt && (run.paused || failedBefore(i))
    const word: StageWord = s.status === 'Done' ? 'Successful' : s.status === 'Running' ? 'Running' : s.status === 'Failed' ? 'Failed' : held ? 'Blocked' : run.endedAt ? 'Skipped' : 'Planned'
    const bullets: Bullet[] = held
      ? [...(run.paused ? [{ text: run.pauseReason ? `Run paused: ${run.pauseReason}` : 'Run paused', mark: 'waiting' as const, word: 'held' }] : []),
         ...(failedStep ? [{ text: `${failedStep.stepCode} failed: resolve it first`, mark: 'failed' as const, word: 'failed' }] : []),
         ...mine.slice(0, 1).map(x => ({ text: x.title, mark: 'waiting' as const, word: 'queued' }))].slice(0, 3)
      : mine.slice(0, 3).map(x => ({ text: x.title, mark: markOf(x.status), word: MARK_WORD[markOf(x.status)] }))
    const done = s.done + s.skipped
    const summary = s.status === 'Done' ? `${done} / ${s.steps} steps done`
      : s.status === 'Pending' ? (held ? `${s.steps} ${s.steps === 1 ? 'step' : 'steps'} held` : s.section === 'Rollback' ? 'Only if called' : run.endedAt ? 'Not started' : `${s.steps} ${s.steps === 1 ? 'step' : 'steps'} planned`)
      : [s.done + s.skipped > 0 ? `${s.done + s.skipped} done` : '', s.running > 0 ? `${s.running} running` : '', s.failed > 0 ? `${s.failed} failed` : '', s.scheduled > 0 ? `${s.scheduled} queued` : ''].filter(Boolean).join(' · ')
    const span = s.actualStartAt
      ? `${hm(Date.parse(s.actualStartAt))} → ${s.actualEndAt ? hm(Date.parse(s.actualEndAt)) : 'now'} UTC · ${Math.max(0, Math.round(((s.actualEndAt ? Date.parse(s.actualEndAt) : nowMs) - Date.parse(s.actualStartAt)) / 60000))}m`
      : s.plannedStartAt ? `${held ? 'Target' : 'Planned'} ${hm(Date.parse(s.plannedStartAt))} → ${hm(Date.parse(s.plannedEndAt ?? s.plannedStartAt))} UTC` : 'Not scheduled'
    return { section: s.section, title: sectionTitle(s.section), word, tone: chipTone(word), counts: s, bullets, summary, span, announce: `${sectionTitle(s.section)}, ${word}. ${summary}.` }
  })
}

/** The stage to select when the person has not chosen one: the first that is running or failed, else the first not done, else the last. */
export function defaultStage(cards: StageCard[]): string | null {
  return (cards.find(c => c.word === 'Running' || c.word === 'Failed') ?? cards.find(c => c.word !== 'Successful' && c.section !== 'Rollback') ?? cards[cards.length - 1])?.section ?? null
}

// ---- steps ------------------------------------------------------------------------------------------------------------------------------------
export interface StepState { word: 'Successful' | 'Running' | 'Failed' | 'Skipped' | 'Queued'; tone: Tone; waitingOn: string[] }
export function stepState(s: RunStepRow, doneCodes: Set<string>): StepState {
  const word = s.status === 'Done' ? 'Successful' : s.status === 'Running' ? 'Running' : s.status === 'Failed' ? 'Failed' : s.status === 'Skipped' ? 'Skipped' : 'Queued'
  return { word, tone: chipTone(word), waitingOn: word === 'Queued' ? s.dependsOn.filter(d => !doneCodes.has(d)) : [] }
}
export const doneCodesOf = (steps: RunStepRow[]) => new Set(steps.filter(s => s.status === 'Done' || s.status === 'Skipped').map(s => s.stepCode))

/** Seconds a step has taken: its whole time when it has ended, so far when it is running, null when it has not started. */
export function stepSeconds(s: RunStepRow, nowMs: number): number | null {
  if (!s.actualStartAt) return null
  return ((s.actualEndAt ? Date.parse(s.actualEndAt) : nowMs) - Date.parse(s.actualStartAt)) / 1000
}

/** The strategy a step rolls out with: the run's deployment whose service the step's title names (the runbook does not link a step to a deployment, Q-98b). */
export function strategyFor(step: Pick<RunStepRow, 'title'>, deployments: Deployment[]): string | null {
  const t = step.title.toLowerCase()
  const d = deployments.find(x => t.includes(x.serviceKey.toLowerCase()) || t.includes(x.serviceName.toLowerCase()))
  if (!d) return null
  return d.strategy === 'Canary' && d.state === 'Running' ? `Canary · ${d.trafficPercent}%` : d.strategy
}

/** The newest thing that happened in the run, for the table's footer line: "R-002 done 14:32:08". */
export function lastEvent(steps: RunStepRow[]): { at: number; text: string } | null {
  let best: { at: number; text: string } | null = null
  for (const s of steps) {
    for (const [t, what] of [[s.actualStartAt, 'started'], [s.actualEndAt, s.status === 'Failed' ? 'failed' : s.status === 'Skipped' ? 'skipped' : 'done']] as const) {
      if (!t) continue
      const at = Date.parse(t)
      if (!best || at >= best.at) best = { at, text: `${s.stepCode} ${what}` }
    }
  }
  return best
}

// ---- the run clock and the bars (REOS-128) ----------------------------------------------------------------------------------------------------
export interface Clock { windowSec: number | null; toDeadlineSec: number | null; deadlineClose: boolean; deadlinePassed: boolean }
export function clockOf(fc: { asOf: string; windowClosesInSec: number | null; rollbackDeadline: string | null } | null, nowMs: number): Clock {
  const sinceAsOf = fc ? (nowMs - Date.parse(fc.asOf)) / 1000 : 0
  const windowSec = fc?.windowClosesInSec != null ? fc.windowClosesInSec - sinceAsOf : null
  const toDeadlineSec = fc?.rollbackDeadline ? (Date.parse(fc.rollbackDeadline) - nowMs) / 1000 : null
  return { windowSec, toDeadlineSec, deadlineClose: toDeadlineSec !== null && toDeadlineSec < 1800, deadlinePassed: toDeadlineSec !== null && toDeadlineSec < 0 }
}

export interface BarRow { plan: [number, number]; actual: [number, number] | null; running: boolean; forecast: [number, number] | null; forecastLate: boolean }
/** x positions (0 to `width`) of one step's bars on the window-scaled axis: planned, actual so far, and the forecast of what is still to come. */
export function bars(s: RunStepRow, f: { start: string | null; end: string | null } | undefined, win: [number, number], nowMs: number, width: number): BarRow {
  const span = Math.max(1, win[1] - win[0])
  const x = (ms: number) => Math.max(0, Math.min(width, ((ms - win[0]) / span) * width))
  const seg = (a: number, b: number): [number, number] => { const from = x(a); return [from, Math.max(from + 2, x(b))] }
  const actual = s.actualStartAt ? seg(Date.parse(s.actualStartAt), s.actualEndAt ? Date.parse(s.actualEndAt) : nowMs) : null
  const running = s.status === 'Running'
  const forecast = running && f?.end ? seg(nowMs, Date.parse(f.end)) : s.status === 'Scheduled' && f?.start && f.end ? seg(Date.parse(f.start), Date.parse(f.end)) : null
  return { plan: seg(Date.parse(s.plannedStartAt), Date.parse(s.plannedEndAt)), actual, running, forecast, forecastLate: s.status === 'Scheduled' && forecast !== null }
}
export const axisX = (ms: number, win: [number, number], width: number) => Math.max(0, Math.min(width, ((ms - win[0]) / Math.max(1, win[1] - win[0])) * width))

/** The one word for the run's chip. */
export function runWord(r: Pick<RunStages, 'paused' | 'endedAt' | 'outcome'>): { word: string; tone: Tone } {
  if (r.endedAt) { const w = r.outcome === 'Completed' ? 'Successful' : r.outcome === 'RolledBack' ? 'Rolled back' : 'Aborted'; return { word: w, tone: chipTone(w) } }
  return r.paused ? { word: 'Paused', tone: 'warning' } : { word: 'Running', tone: 'info' }
}
