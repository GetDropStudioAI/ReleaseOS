import { localClock } from '../shell/clock.ts'
import type { Breach, ControlAction, Deployment, Metrics, MetricPoint, Monitoring } from './deployApi.ts'

/** The pure parts of the Deployments screens (REOS-100, REOS-140): keys, ranges, chart scales and the words under each metric. Tested without a browser. */

/** DEP-nnnn in the design. The model has no sequence number, so the key is the last six characters of the deployment's id (QUESTIONS Q-1000). */
export const depKey = (id: string) => `DEP-${id.replace(/[^0-9a-z]/gi, '').slice(-6).toUpperCase()}`

export const RANGES = [
  { value: '15', label: 'Last 15 minutes' }, { value: '30', label: 'Last 30 minutes' }, { value: '60', label: 'Last hour' },
  { value: '360', label: 'Last 6 hours' }, { value: '1440', label: 'Last 24 hours' },
]
export const DEFAULT_RANGE = '30'

const iso = (ms: number) => new Date(ms).toISOString().replace(/\.\d{3}Z$/, 'Z')
const pad = (n: number) => String(n).padStart(2, '0')
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** "14:32 UTC". */
export function utcHM(ms: number): string { const d = new Date(ms); return `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())} UTC` }
/** "14:32:08", the event log's time column (the column heading says UTC). */
export function utcHMS(ms: number): string { const d = new Date(ms); return `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}` }
/** "23 Oct 2026, 14:32 UTC" with the person's time beside it (D44): `local` is null for a person who works in UTC. */
export function stampOf(isoTime: string | null, zone: string): { utc: string; local: string | null } {
  if (!isoTime) return { utc: '', local: null }
  const ms = Date.parse(isoTime), d = new Date(ms)
  return { utc: `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}, ${utcHM(ms)}`, local: localClock(ms, zone) }
}
export const stampText = (s: { utc: string; local: string | null }) => (s.local ? `${s.utc} (${s.local} for you)` : s.utc)

/** The right-hand end of every chart: the newest sample (a tool's clock may run ahead of ours, Q-1111), else the end of the deployment, else now. */
export function anchorOf(m: Monitoring, nowMs: number): number {
  const times = m.latest.map(l => Date.parse(l.at))
  if (times.length > 0) return Math.max(...times)
  return m.deployment.finishedAt ? Date.parse(m.deployment.finishedAt) : nowMs
}
export const rangeStart = (anchorMs: number, minutes: number) => iso(anchorMs - minutes * 60000)

/** The next "nice" ceiling for an axis: 1, 2, 2.5, 5 or 10 times a power of ten, never below `value`. */
export function niceMax(value: number): number {
  if (!(value > 0)) return 1
  const p = 10 ** Math.floor(Math.log10(value)), f = value / p
  return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * p
}

export interface Pt { t: number; v: number }
export function pointsOf(points: MetricPoint[] | undefined, pick: (p: MetricPoint) => number | null): Pt[] {
  return (points ?? []).flatMap(p => { const v = pick(p); return v === null ? [] : [{ t: Date.parse(p.at), v }] })
}

/** Ticks of a time axis: `n` evenly spaced instants from `from` to `to`, inclusive. */
export const timeTicks = (from: number, to: number, n = 4) => Array.from({ length: n }, (_, i) => from + ((to - from) * i) / (n - 1))

/** The y ceiling of a chart: the tallest value or threshold, with a fifth of headroom, on a nice number. */
export function yMax(series: Pt[][], threshold: number | null): number {
  const tallest = Math.max(0, threshold ?? 0, ...series.flatMap(s => s.map(p => p.v)))
  return niceMax(tallest * 1.2)
}

export const trim = (n: number, digits = 2) => String(Number(n.toFixed(digits)))

export type Tone = 'success' | 'info' | 'warning' | 'danger' | 'neutral'
export interface CardText { value: string; unit?: string; detail: string; tone: Tone }

const latestOf = (m: Monitoring, series: 'Canary' | 'Stable') => m.latest.find(l => l.series === series)
/** The series the cards read: a canary's Canary series, otherwise whichever the tool pushes (Stable). */
const primary = (m: Monitoring) => (m.canary.isCanary ? latestOf(m, 'Canary') : (latestOf(m, 'Stable') ?? latestOf(m, 'Canary')))

export function trafficCard(m: Monitoring, zone: string): CardText {
  const d = m.deployment
  const pods = d.readyPods !== null && d.desiredPods !== null ? `${d.readyPods} / ${d.desiredPods} pods` : undefined
  let detail = `${d.strategy} rollout`
  if (m.canary.isCanary && m.canary.nextTrafficPercent !== null && m.canary.nextPromotionAt && d.state === 'Running') {
    const at = Date.parse(m.canary.nextPromotionAt)
    detail = `Next: ${trim(m.canary.nextTrafficPercent)}% at ${utcHM(at)}${localClock(at, zone) ? ` (${localClock(at, zone)} for you)` : ''}`
  } else if (m.canary.isCanary && m.canary.nextTrafficPercent === null && d.state === 'Running') detail = 'On the last step'
  else if (d.state !== 'Running') detail = `Deployment ${d.state === 'RolledBack' ? 'rolled back' : d.state.toLowerCase()}`
  return { value: `${trim(d.trafficPercent)}%`, unit: pods, detail, tone: d.state === 'Paused' ? 'warning' : 'neutral' }
}

export function errorCard(m: Monitoring): CardText {
  const s = primary(m), limit = m.thresholds.errorRateMaxPct
  if (!s || s.errorRatePct === null) return { value: '–', detail: 'No error rate reported yet', tone: 'neutral' }
  if (limit === null) return { value: `${trim(s.errorRatePct)}%`, detail: 'No rollback threshold set', tone: 'neutral' }
  const over = s.errorRatePct > limit
  return { value: `${trim(s.errorRatePct)}%`, detail: `${over ? 'Above' : 'Below'} ${trim(limit)}% rollback threshold`, tone: over ? 'danger' : 'success' }
}

export function latencyCard(m: Monitoring): CardText {
  const c = latestOf(m, 'Canary'), s = latestOf(m, 'Stable'), p = primary(m)
  if (!p || p.latencyP95Ms === null) return { value: '–', detail: 'No latency reported yet', tone: 'neutral' }
  const limit = m.thresholds.latencyP95MaxMs
  const over = limit !== null && p.latencyP95Ms > limit
  if (m.canary.isCanary && c?.latencyP95Ms != null && s?.latencyP95Ms != null) {
    const delta = Math.round(c.latencyP95Ms - s.latencyP95Ms)
    return { value: String(Math.round(p.latencyP95Ms)), unit: 'ms', detail: delta === 0 ? 'Same as stable version' : `${delta < 0 ? '−' : '+'}${Math.abs(delta)} ms vs. stable version`, tone: over ? 'danger' : delta > 0 ? 'warning' : 'success' }
  }
  return { value: String(Math.round(p.latencyP95Ms)), unit: 'ms', detail: limit === null ? 'No latency threshold set' : `${over ? 'Above' : 'Below'} ${trim(limit)} ms threshold`, tone: over ? 'danger' : 'success' }
}

export function availabilityCard(m: Monitoring): CardText {
  const s = primary(m), slo = m.slo.availabilityPct
  if (!s || s.availabilityPct === null) return { value: '–', detail: 'No availability reported yet', tone: 'neutral' }
  const value = `${trim(s.availabilityPct, 3)}%`
  if (slo === null) return { value, detail: 'No SLO set for this environment', tone: 'neutral' }
  const met = m.sloStatus?.availability
  if (met?.meets === true) return { value, detail: `SLO ${trim(slo)}% · Met over ${m.sloStatus!.days} days`, tone: 'success' }
  if (met?.meets === false) return { value, detail: `SLO ${trim(slo)}% · Not met over ${m.sloStatus!.days} days`, tone: 'danger' }
  return { value, detail: `SLO ${trim(slo)}% · ${s.availabilityPct >= slo ? 'Latest sample meets it' : 'Latest sample is below it'}`, tone: s.availabilityPct >= slo ? 'success' : 'danger' }
}

/** The automatic-rollback banner: whether it is armed and whether any gate (a threshold on the canary) is failing or being watched. */
export function rollbackBanner(m: Monitoring): { tone: Tone; title: string; body?: string } {
  const d = m.deployment
  if (d.state === 'RolledBack') return { tone: 'info', title: 'Rolled back', body: 'The deployment tool reported this deployment as rolled back.' }
  const open = m.breaches.filter(b => b.series === 'Canary' && b.status !== 'Resolved')
  const confirmed = open.find(b => b.status === 'Breached'), watching = open.find(b => b.status === 'Watching')
  const gate = (b: Breach) => `${metricName(b.metric)} ${trim(b.lastValue)}${metricUnit(b.metric)} against ${trim(b.thresholdValue)}${metricUnit(b.metric)}`
  const armed = m.rollback.armed
  const head = armed ? 'Automatic rollback armed' : 'Automatic rollback is off'
  if (confirmed) return { tone: 'danger', title: `${head} · Threshold breached`, body: `${gate(confirmed)} since ${utcHM(Date.parse(confirmed.startedAt))}.` }
  if (watching) return { tone: 'warning', title: `${head} · A threshold is being watched`, body: `${gate(watching)}; it rolls back if it stays there for ${watching.forSeconds} s.` }
  if (armed) return { tone: 'success', title: `${head} · All SLO gates passing` }
  return { tone: 'neutral', title: head, body: m.rollback.whyNot ? `${m.rollback.whyNot}.` : 'Arm it from the deployment to roll back automatically on a confirmed breach.' }
}

export const metricName = (metric: string) => (metric === 'ErrorRate' ? 'Error rate' : metric === 'LatencyP95' ? 'p95 latency' : metric === 'Availability' ? 'Availability' : metric)
export const metricUnit = (metric: string) => (metric === 'LatencyP95' ? ' ms' : '%')

/** The words on the health chip of the monitored deployment (other services in the table do not report health, Q-1000). */
export function healthOf(m: Monitoring): 'Healthy' | 'At risk' | 'Failing' | 'Unknown' {
  if (m.breaches.some(b => b.status === 'Breached' && b.series === 'Canary')) return 'Failing'
  if (m.canary.atRisk || m.breaches.some(b => b.status === 'Watching' && b.series === 'Canary')) return 'At risk'
  if (m.canary.result === 'Unknown' && m.latest.length === 0) return 'Unknown'
  return 'Healthy'
}

/** A pause that the tool has not reported yet (the call does not change the state, Q-1112): the newest control action decides what the page says. */
export function pendingControl(actions: ControlAction[], state: string): ControlAction | null {
  const last = actions[0]
  if (!last) return null
  if (last.status === 'Pending') return last
  if (last.status === 'Succeeded' && ((last.action === 'Pause' && state === 'Running') || (last.action === 'Resume' && state === 'Paused'))) return last
  return null
}

/** "Checkout API · Production EU · v3.18.0": the sentence the tab title and the screen reader's summary use. */
export const deploymentName = (d: Deployment) => `${d.serviceName} ${d.releaseVersion} to ${d.environmentName}`

export const LEVEL_WORD: Record<string, string> = { Info: 'INFO', Warning: 'WARN', Error: 'ERROR' }

/** One sentence for the chart's accessible name and its tooltip line. */
export function chartSummary(title: string, unit: string, canary: Pt[], stable: Pt[], threshold: number | null): string {
  const last = (s: Pt[]) => (s.length ? `${trim(s[s.length - 1].v)}${unit}` : 'no data')
  return `${title}: canary ${last(canary)}, stable ${last(stable)}${threshold !== null ? `, threshold ${trim(threshold)}${unit}` : ''}`
}

/** The metrics response is the chart's whole input; this says whether it has anything to draw. */
export const hasSeries = (m: Metrics | null) => !!m && m.series.some(s => s.points.length > 0)

/** The state words of a deployment (RolledBack reads "Rolled back") and the tone of each; Paused is a warning, as a held rollout needs a person's attention. */
export const stateWord = (state: string) => (state === 'RolledBack' ? 'Rolled back' : state)
export function stateTone(state: string): Tone {
  switch (state) {
    case 'Successful': return 'success'
    case 'Running': return 'info'
    case 'Paused': return 'warning'
    case 'Failed': case 'RolledBack': return 'danger'
    default: return 'neutral'
  }
}
