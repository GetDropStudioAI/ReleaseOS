/**
 * Analytics pure logic (REOS-47): response normalisation, the finding that titles each chart, the six headline figures, the per-step aggregation for M7,
 * and the numbers behind each chart's "View data" table. No DOM, no React, no ECharts: unit-tested with `npm test` (tests-unit/, node --test).
 * Imports use the .ts extension so Node can run this file directly (tsconfig allowImportingTsExtensions).
 */

export interface MetricColumn { name: string; unit: string }
/** GET /api/v1/analytics/{m}: rows are objects keyed by camelCased record members (Q-046d), in SQL column order. */
export interface MetricResponse { metric: string; name: string; title: string; unit: string; columns: MetricColumn[]; from: string; to: string; asOf: string; rows: Record<string, unknown>[] }
/** A row keyed by the SQL column name (the names in `columns`), whatever the JSON keys were. */
export type Rec = Record<string, unknown>
export interface Metric { id: string; title: string; unit: string; columns: MetricColumn[]; from: string; to: string; asOf: string; recs: Rec[]; cells: unknown[][] }

export const NO_DATA = 'No data in this window'

/** JSON keys are camelCased record members ("d1_3", "lt1d") that do not derive from the column names, so rows are read by position (SQL column order, which the API preserves). */
export function normalize(r: MetricResponse): Metric {
  const cells = r.rows.map(row => Object.values(row))
  const recs = cells.map(c => Object.fromEntries(r.columns.map((col, i) => [col.name, c[i] ?? null])) as Rec)
  return { id: r.metric, title: r.title, unit: r.unit, columns: r.columns, from: r.from, to: r.to, asOf: r.asOf, recs, cells }
}

/** Column header shared by the CSV, the XLSX (server) and the data table: "median_h (hours)". */
export const columnHeader = (c: MetricColumn) => (c.unit ? `${c.name} (${c.unit})` : c.name)

// ---- small helpers -------------------------------------------------------------------------------------------------------------------
const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null)
const n0 = (v: unknown): number => num(v) ?? 0
const str = (v: unknown): string => (v == null ? '' : String(v))
/** At most `d` decimals, no trailing zeros: 5 -> "5", 5.42 -> "5.4". */
export const fmt = (v: number, d = 1): string => String(Number(v.toFixed(d)))
const plural = (n: number, one: string, many = one + 's') => (n === 1 ? one : many)
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
/** "2026-05" -> "May 2026"; anything else is returned as is. */
export function monthLabel(m: string): string {
  const x = /^(\d{4})-(\d{2})$/.exec(m)
  return x && +x[2] >= 1 && +x[2] <= 12 ? `${MONTHS[+x[2] - 1]} ${x[1]}` : m
}
const monthShort = (m: string) => { const x = /^\d{4}-(\d{2})$/.exec(m); return x && +x[2] >= 1 && +x[2] <= 12 ? MONTHS[+x[2] - 1] : m }
export { monthShort }
const orList = (xs: string[]) => (xs.length <= 1 ? xs.join('') : `${xs.slice(0, -1).join(', ')} or ${xs[xs.length - 1]}`)
const numWord = (n: number) => (n === 1 ? 'One' : String(n))
const median = (xs: number[]) => { const s = [...xs].sort((a, b) => a - b); const m = s.length >> 1; return s.length === 0 ? null : s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2 }

/** The row maximising `key` (first wins on a tie); rows with a null key are skipped. */
function maxBy<T>(xs: T[], key: (x: T) => number | null): T | null {
  let best: T | null = null, bk = -Infinity
  for (const x of xs) { const k = key(x); if (k !== null && k > bk) { best = x; bk = k } }
  return best
}

// ---- M7 aggregation ------------------------------------------------------------------------------------------------------------------
export interface StepAgg { step: string; section: string; runs: number; avg: number; worst: number }
/** Overrun per runbook step code across live runs: average and worst, in minutes. Steps with no measured overrun are dropped. Ordered as the step codes sort. */
export function aggregateSteps(recs: Rec[]): StepAgg[] {
  const by = new Map<string, { section: string; xs: number[] }>()
  for (const r of recs) {
    const o = num(r.overrun_min); if (o === null) continue
    const k = str(r.StepCode); const e = by.get(k) ?? { section: str(r.Section), xs: [] }
    e.xs.push(o); by.set(k, e)
  }
  return [...by.entries()].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
    .map(([step, e]) => ({ step, section: e.section, runs: e.xs.length, avg: e.xs.reduce((a, b) => a + b, 0) / e.xs.length, worst: Math.max(...e.xs) }))
}

// ---- findings: one pure function per chart -------------------------------------------------------------------------------------------
export function findingM2(recs: Rec[]): string {
  if (!recs.length) return NO_DATA
  const late = recs.map(r => n0(r.slip_days)).filter(s => s > 0)
  if (!late.length) return `All ${recs.length} trains shipped on or before their baseline`
  const distinct = [...new Set(late)].sort((a, b) => a - b)
  const by = distinct.length <= 3 ? `by ${orList(distinct.map(String))} ${plural(distinct[distinct.length - 1], 'day')}` : `by up to ${distinct[distinct.length - 1]} days`
  return `${late.length} of ${recs.length} trains shipped late, ${late.length === 1 ? by : 'each ' + by}`
}
export function findingM3(recs: Rec[]): string {
  const top = maxBy(recs, r => num(r.median_h))
  if (!top) return NO_DATA
  const p90 = num(top.p90_h)
  return `${str(top.GateName)} is the slowest gate: median ${fmt(n0(top.median_h) / 24)} days${p90 === null ? '' : `, p90 ${fmt(p90 / 24)}`}`
}
export function findingM4(recs: Rec[]): string {
  const rated = recs.filter(r => num(r.first_pass_pct) !== null)
  if (!rated.length) return NO_DATA
  const low = rated.reduce((a, b) => (n0(b.first_pass_pct) < n0(a.first_pass_pct) ? b : a))
  if (n0(low.first_pass_pct) >= 100) return `Every gate passed first time (100%)`
  return `${str(low.GateName)} has the lowest first-pass rate: ${fmt(n0(low.first_pass_pct))}%`
}
export const lateShare = (r: Rec) => (n0(r.gates) > 0 ? n0(r.late) / n0(r.gates) : null)
export function findingM5(recs: Rec[]): string {
  if (!recs.length) return NO_DATA
  const top = maxBy(recs, lateShare)
  if (!top || n0(top.late) === 0) return 'No gate was certified late'
  const share = n0(top.late) / n0(top.gates)
  return share > 0.5 ? `${str(top.GateName)} is certified late on more than half of trains` : `${str(top.GateName)} is certified late most often: ${n0(top.late)} of ${n0(top.gates)} (${fmt(share * 100, 0)}%)`
}
export function findingM6(recs: Rec[]): string {
  if (!recs.length) return NO_DATA
  const changed = recs.filter(r => n0(r.added) + n0(r.removed) > 0)
  if (!changed.length) return `No train changed scope after Code Freeze`
  const top = maxBy(changed, r => n0(r.added) + n0(r.removed))!
  return `${changed.length} of ${recs.length} trains changed scope after Code Freeze; most: ${str(top.Title)} (+${n0(top.added)}, -${n0(top.removed)})`
}
export function findingM7(recs: Rec[]): string {
  const agg = aggregateSteps(recs)
  if (!agg.length) return NO_DATA
  const top = agg.reduce((a, b) => (b.avg > a.avg ? b : a))
  if (top.avg <= 0) return 'No runbook step ran over its plan'
  return `${top.step} runs long most: ${fmt(top.avg)} min over on average, ${fmt(top.worst)} at worst`
}
export function findingM8(recs: Rec[]): string {
  const top = maxBy(recs, r => num(r.total_overrun_min))
  if (!top) return NO_DATA
  if (n0(top.total_overrun_min) <= 0) return 'No live run overran its plan'
  const worst = str(top.worst_step)
  return `${str(top.Title)} overran most: ${fmt(n0(top.total_overrun_min))} min in total${worst ? `, worst step ${worst}` : ''}`
}
const SEVERITY_RANK = ['Critical', 'High', 'Medium', 'Low']
export const blockerTotal = (r: Rec) => n0(r.lt_1d) + n0(r.d1_3) + n0(r.d3_7) + n0(r.ge_7d)
export function findingM9(recs: Rec[]): string {
  const total = recs.reduce((a, r) => a + blockerTotal(r), 0)
  if (!recs.length || total === 0) return recs.length ? 'No open blockers' : NO_DATA
  const old = recs.filter(r => n0(r.ge_7d) > 0).sort((a, b) => SEVERITY_RANK.indexOf(str(a.Severity)) - SEVERITY_RANK.indexOf(str(b.Severity)))[0]
  if (old) {
    const n = n0(old.ge_7d), days = fmt(n0(old.oldest_days), 0)
    return `${numWord(n)} ${str(old.Severity)} ${plural(n, 'blocker')} ${n === 1 ? 'has' : 'have'} been open 7 days or more, the oldest ${days} days`
  }
  return `${total} open ${plural(total, 'blocker')}, none older than 7 days`
}
export function findingM10(recs: Rec[]): string {
  const r = recs[0]
  if (!r || n0(r.completed) === 0) return NO_DATA
  const done = n0(r.completed), bad = n0(r.with_issues) + n0(r.unsuccessful)
  const roll = num(r.rollback_pct)
  if (bad === 0) return `All ${done} trains closed Successful${roll ? `; ${fmt(roll)}% rolled back` : ''}`
  return `${bad} of ${done} trains closed with issues or unsuccessfully${roll === null ? '' : `; ${fmt(roll)}% rolled back`}`
}
export function findingM11(recs: Rec[]): string {
  if (!recs.length) return NO_DATA
  const total = recs.reduce((a, r) => a + n0(r.overrides), 0)
  if (total === 0) return 'No freeze was overridden in this window'
  const top = maxBy(recs, r => n0(r.overrides))!
  return `${total} freeze ${plural(total, 'override')}; ${str(top.Name)} most (${n0(top.overrides)})`
}
export function findingM13(recs: Rec[]): string {
  const gates = recs.reduce((a, r) => a + n0(r.compliance_gates), 0)
  if (!recs.length || gates === 0) return NO_DATA
  const w = recs.reduce((a, r) => a + n0(r.waived), 0)
  if (w === 0) return `No Compliance gate was waived (${gates} certified)`
  const peak = maxBy(recs, r => num(r.waived_pct))!
  return `${w} of ${gates} Compliance gates were waived (${fmt(w / gates * 100)}%); peak ${monthLabel(str(peak.month))} at ${fmt(n0(peak.waived_pct))}%`
}
export function findingM14(recs: Rec[]): string {
  if (!recs.length) return NO_DATA
  const links = recs.reduce((a, r) => a + n0(r.links), 0)
  if (links === 0) return 'No external links yet'
  const bad = recs.reduce((a, r) => a + n0(r.mismatch) + n0(r.broken), 0)
  return bad === 0 ? `All ${links} external links are in sync` : `${bad} of ${links} external links are mismatched or broken`
}
export function findingM15(recs: Rec[]): string {
  const months = recs.filter(r => r.month != null)
  if (!months.length) return NO_DATA
  const peak = months.reduce((a, b) => (n0(b.completed) >= n0(a.completed) ? b : a))
  const lead = num(peak.median_lead_days)
  return `Throughput peaked at ${n0(peak.completed)} ${plural(n0(peak.completed), 'train')} in ${monthLabel(str(peak.month))}${lead === null ? '' : `; median lead time ${fmt(lead)} days that month`}`
}

// ---- the six headline figures --------------------------------------------------------------------------------------------------------
export interface Headline { key: string; label: string; value: string; sub: string; tone: 'ok' | 'warn' | 'bad' | 'none' }
const NONE = '—'
/** `m` holds whichever metrics loaded; a missing one gives an honest dash and a reason instead of a made-up zero. */
export function headlines(m: Partial<Record<'M1' | 'M2' | 'M10' | 'M12' | 'M13', Rec[]>>): Headline[] {
  const H = (key: string, label: string, value: string, sub: string, tone: Headline['tone'] = 'none'): Headline => ({ key, label, value, sub, tone })
  const m1 = m.M1?.[0], m2 = m.M2, m10 = m.M10?.[0], m12 = m.M12?.[0], m13 = m.M13

  const onTime = m1 && n0(m1.completed) > 0 && num(m1.on_time_pct) !== null
    ? H('on-time', 'On time', `${fmt(n0(m1.on_time_pct))}%`, `${n0(m1.on_time)} of ${n0(m1.completed)} at or before baseline`)
    : H('on-time', 'On time', NONE, m.M1 ? NO_DATA : 'Not loaded')

  const late = m2?.map(r => n0(r.slip_days)).filter(s => s > 0)
  const slip = !m2 ? H('slip', 'Slip when late', NONE, 'Not loaded')
    : late!.length === 0 ? H('slip', 'Slip when late', m2.length ? '0 d' : NONE, m2.length ? `no late trains of ${m2.length}` : NO_DATA)
      : H('slip', 'Slip when late', `${fmt(median(late!)!)} d`, `median of ${late!.length} late ${plural(late!.length, 'train')}`, 'warn')

  const done = m10 ? n0(m10.completed) : 0
  const notOk = m10 ? n0(m10.with_issues) + n0(m10.unsuccessful) : 0
  const cfail = m10 && done > 0 && num(m10.change_fail_pct) !== null
    ? H('change-fail', 'Change fail', `${fmt(n0(m10.change_fail_pct))}%`, `${notOk} of ${done} not Successful`)
    : H('change-fail', 'Change fail', NONE, m.M10 ? NO_DATA : 'Not loaded')
  const rollN = m10 && num(m10.rollback_pct) !== null ? Math.round(n0(m10.rollback_pct) * done / 100) : 0
  const roll = m10 && done > 0 && num(m10.rollback_pct) !== null
    ? H('rollback', 'Rollback', `${fmt(n0(m10.rollback_pct))}%`, `${rollN} of ${done} ${plural(done, 'train')} rolled back`)
    : H('rollback', 'Rollback', NONE, m.M10 ? NO_DATA : 'Not loaded')

  const comms = m12 && n0(m12.scheduled) > 0 && num(m12.on_time_pct) !== null
    ? H('comms', 'Comms on time', `${fmt(n0(m12.on_time_pct))}%`, `${n0(m12.on_time)} of ${n0(m12.scheduled)} scheduled sends`)
    : H('comms', 'Comms on time', NONE, m.M12 ? NO_DATA : 'Not loaded')

  const gates = m13?.reduce((a, r) => a + n0(r.compliance_gates), 0) ?? 0
  const waived = m13?.reduce((a, r) => a + n0(r.waived), 0) ?? 0
  const waive = m13 && gates > 0
    ? H('waived', 'Waived gates', String(waived), `of ${gates} Compliance ${plural(gates, 'gate')} · ${fmt(waived / gates * 100)}%`)
    : H('waived', 'Waived gates', NONE, m13 ? NO_DATA : 'Not loaded')
  return [onTime, slip, cfail, roll, comms, waive]
}

// ---- the "View data" table behind each chart -----------------------------------------------------------------------------------------
export interface DataTable { headers: string[]; rows: string[][]; numeric: boolean[] }
/** What an empty cell shows; the table renders it with a spoken "none". */
export const NONE_CELL = '—'
const cell = (v: unknown, d = 1) => (v == null ? NONE_CELL : typeof v === 'number' ? fmt(v, d) : String(v))
/** Readable names for the SQL columns in the on-screen table; the CSV and XLSX keep the raw names (columnHeader). */
const LABELS: Record<string, string> = {
  completed: 'Completed', on_time: 'On time', on_time_pct: 'On time', Title: 'Train', PlannedReleaseDate: 'Planned release', actual: 'Actual release', slip_days: 'Slip',
  GateName: 'Gate', samples: 'Gates timed', median_h: 'Median', p90_h: 'p90', gates: 'Gates', first_pass_pct: 'First pass', waived: 'Waived', late: 'Certified late',
  avg_days_late: 'Average lateness', at_freeze: 'At freeze', added: 'Added', removed: 'Removed', StepCode: 'Step', Section: 'Section', start_late_min: 'Start late',
  overrun_min: 'Overrun', steps: 'Steps', total_overrun_min: 'Total overrun', worst_step: 'Worst step', worst_overrun_min: 'Worst overrun', Severity: 'Severity',
  lt_1d: 'Open under 1 day', d1_3: 'Open 1–3 days', d3_7: 'Open 3–7 days', ge_7d: 'Open 7 days or more', oldest_days: 'Oldest', successful: 'Successful',
  with_issues: 'With issues', unsuccessful: 'Unsuccessful', rollback_pct: 'Rollback', change_fail_pct: 'Change fail', Name: 'Freeze window', overrides: 'Overrides',
  avg_ttl_h: 'Average override length', scheduled: 'Scheduled', missed: 'Missed', month: 'Month', compliance_gates: 'Compliance gates', waived_pct: 'Waived',
  SourceSystem: 'Source system', links: 'Links', in_sync: 'In sync', mismatch: 'Mismatch', broken: 'Broken', stalest_min: 'Stalest', open_alerts: 'Open alerts',
  median_lead_days: 'Median lead time',
}
const MEASURES = new Set(['hours', 'days', 'minutes', '%'])
/** "Median (hours)", "Gates timed": a readable name, with the unit only where it is a measure (a count's unit is already in its name). */
export const humanHeader = (c: MetricColumn) => { const l = LABELS[c.name] ?? c.name.replace(/_/g, ' '); return MEASURES.has(c.unit) ? `${l} (${c.unit})` : l }
/** The numbers the chart draws, formatted the same as its labels; M7 is aggregated per step (the raw 176-row table is what the CSV/XLSX carries). */
export function chartTable(id: string, recs: Rec[], columns: MetricColumn[]): DataTable {
  if (id === 'M7') {
    const agg = aggregateSteps(recs)
    return { headers: ['Step', 'Section', 'Runs', 'Average overrun (min)', 'Worst overrun (min)'], numeric: [false, false, true, true, true],
      rows: agg.map(a => [a.step, a.section, String(a.runs), fmt(a.avg), fmt(a.worst)]) }
  }
  return { headers: columns.map(humanHeader), numeric: columns.map(c => c.unit !== '' && c.unit !== 'date' && c.unit !== 'month'), rows: recs.map(r => columns.map(c => (id === 'M9' && r[c.name] === 0 && c.unit === 'blockers' ? NONE_CELL : cell(r[c.name])))) }
}
