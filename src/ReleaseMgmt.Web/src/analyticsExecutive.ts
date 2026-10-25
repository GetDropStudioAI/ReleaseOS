/**
 * Executive analytics, pure logic (REOS-121, D49): the VP and Director views read across the workspaces the person belongs to. The query-string builders, the
 * grouping choices, the headline figures and the tables behind each panel. No DOM, no React: unit-tested with `npm test`. Imports use the .ts extension so Node can run this directly.
 */
import { fmt, type Metric, type Rec } from './analyticsFindings.ts'

export type AnalyticsView = 'ops' | 'vp' | 'director'
export const VIEWS: { id: AnalyticsView; label: string }[] = [{ id: 'ops', label: 'Operations' }, { id: 'vp', label: 'VP' }, { id: 'director', label: 'Director' }]
export const parseView = (s: string | null | undefined): AnalyticsView => (s === 'vp' || s === 'director' ? s : 'ops')

export interface ScopeWorkspace { id: string; slug: string; name: string; role: string; current: boolean }
export interface Scope { workspaces: ScopeWorkspace[]; dimensions: string[]; fiscalYearStartMonth: number; quarters: 'calendar' | 'fiscal' }

/** `''` = the current workspace alone (what every request read before); `all` = every workspace of the person's; otherwise one workspace's slug. */
export type WorkspaceChoice = string

/** What a view starts with: Operations reads the current workspace, the executive views read every workspace the person belongs to (D49). */
export const defaultChoice = (view: AnalyticsView): WorkspaceChoice => (view === 'ops' ? '' : 'all')
export const defaultGroup = (view: AnalyticsView): string => (view === 'vp' ? 'workspace,quarter' : view === 'director' ? 'team,type' : '')

/** The grouping choices, as words. A value is the comma-separated `groupBy` the API takes; '' = one row per measure (the portfolio total). */
export const GROUPINGS: { value: string; label: string; views: AnalyticsView[] }[] = [
  { value: 'workspace,quarter', label: 'Workspace and quarter', views: ['vp'] },
  { value: 'quarter', label: 'Quarter', views: ['vp'] },
  { value: 'workspace', label: 'Workspace', views: ['vp', 'director'] },
  { value: 'type,quarter', label: 'Release type and quarter', views: ['vp'] },
  { value: 'segment,quarter', label: 'Client segment and quarter', views: ['vp'] },
  { value: 'team,type', label: 'Team and release type', views: ['director'] },
  { value: 'team', label: 'Team', views: ['director'] },
  { value: 'version', label: 'Product version', views: ['director'] },
  { value: 'team,version', label: 'Team and product version', views: ['director'] },
  { value: 'type', label: 'Release type', views: ['vp', 'director'] },
  { value: 'segment', label: 'Client segment', views: ['vp', 'director'] },
  { value: '', label: 'None: portfolio total', views: ['vp', 'director'] },
]
export const groupingsFor = (view: AnalyticsView) => GROUPINGS.filter(g => g.views.includes(view))
export const groupList = (g: string): string[] => g.split(',').map(x => x.trim()).filter(Boolean)

/** The workspaces parameter: nothing for the current workspace, `all`, or the one chosen slug. */
export function workspacesParam(choice: WorkspaceChoice): string {
  return choice === '' ? '' : `workspaces=${encodeURIComponent(choice)}`
}

/** The query of one metric request. `grouped` metrics (M16 to M24) carry groupBy; M1 to M15 never do (the API refuses it, 400). */
export function execQuery(o: { from?: string; to?: string; now?: string; choice: WorkspaceChoice; groupBy?: string; grouped?: boolean; type?: string }): string {
  const q: string[] = []
  if (o.from && o.to) q.push(`from=${encodeURIComponent(o.from)}`, `to=${encodeURIComponent(o.to)}`)
  if (o.now) q.push(`now=${encodeURIComponent(o.now)}`)
  const w = workspacesParam(o.choice); if (w) q.push(w)
  if (o.grouped && o.groupBy) q.push(`groupBy=${encodeURIComponent(groupList(o.groupBy).join(','))}`)
  if (o.type) q.push(`releaseTypeId=${encodeURIComponent(o.type)}`)
  return q.length ? `?${q.join('&')}` : ''
}

export const portfolioPdfUrl = (o: { from: string; to: string; now: string; choice: WorkspaceChoice }): string =>
  `/api/v1/analytics/portfolio.pdf${execQuery({ from: o.from, to: o.to, now: o.now, choice: o.choice === '' ? 'all' : o.choice })}`
export const portfolioPdfName = (from: string, to: string) => `portfolio-report-${from.replace(/[^0-9A-Za-z-]/g, '')}_${to.replace(/[^0-9A-Za-z-]/g, '')}.pdf`

/** "Cards, Payments (2 of 3 of your workspaces)". */
export function scopeHeading(shown: { name: string }[], total: number): string {
  const names = shown.map(w => w.name).join(', ')
  return `${names || 'No workspace'} (${shown.length} of ${total} of your workspaces)`
}

/** Which workspaces a choice reads, from the person's own list. */
export function chosenWorkspaces(scope: Scope, choice: WorkspaceChoice): ScopeWorkspace[] {
  if (choice === 'all') return scope.workspaces
  if (choice === '') return scope.workspaces.filter(w => w.current)
  return scope.workspaces.filter(w => w.slug === choice)
}

/** The quarter basis in words (Q-121a). */
export function quarterBasis(scope: Pick<Scope, 'quarters' | 'fiscalYearStartMonth'>): string {
  return scope.quarters === 'calendar' ? 'Calendar quarters (2026-Q3 is July to September)' : `Fiscal quarters, the year starts in month ${scope.fiscalYearStartMonth} (FY2027-Q1 is the first quarter of the year that ends in 2027)`
}

// ---- rows ----------------------------------------------------------------------------------------------------------------------------
const num = (v: unknown): number => (typeof v === 'number' && Number.isFinite(v) ? v : 0)
const sum = (rs: Rec[], k: string) => rs.reduce((a, r) => a + num(r[k]), 0)
const pct = (part: number, whole: number): number | null => (whole > 0 ? Math.round(1000 * part / whole) / 10 : null)

/** The group columns of M16 to M24 in the order the API returns them, with the dimension that asks for each. */
export const KEY_COLUMNS: { column: string; dimension: string; header: string }[] = [
  { column: 'workspace', dimension: 'workspace', header: 'Workspace' }, { column: 'team', dimension: 'team', header: 'Team' }, { column: 'release_type', dimension: 'type', header: 'Release type' },
  { column: 'version', dimension: 'version', header: 'Version' }, { column: 'segment', dimension: 'segment', header: 'Client segment' }, { column: 'quarter', dimension: 'quarter', header: 'Quarter' },
]

const HEADERS: Record<string, string> = {
  track: 'Track', completed: 'Completed', measured: 'Measured', on_time: 'On time', on_time_pct: 'On time %', median_slip_days: 'Median slip (d)', max_slip_days: 'Worst slip (d)',
  change_failed: 'Change failed', change_fail_pct: 'Change fail %', restores: 'Rolled-back runs', median_min: 'Median (min)', max_min: 'Longest (min)', exception: 'Exception', events: 'Events',
  releases: 'Releases', step: 'Approval step', samples: 'Samples', median_h: 'Median (h)', p90_h: '90th pct (h)', reason: 'Reason', moves: 'Moves', days_later: 'Days later', days_earlier: 'Days earlier',
  risk_tier: 'Risk tier', open_releases: 'Open', nearest_days: 'Nearest target (d)', past_target: 'Past target', with_blockers: 'With blockers', open_blockers: 'Open blockers', no_rehearsal: 'No rehearsed rollback',
  median_lead_days: 'Median lead (d)', p90_lead_days: '90th pct lead (d)', deployments: 'Deployments', successful: 'Successful', failed: 'Failed', rolled_back: 'Rolled back', success_pct: 'Success %',
  breaches: 'Breaches', availability_breaches: 'Availability breaches', reviews: 'Reviews', with_due_date: 'With due date', held: 'Held', held_by_due: 'Held by due date', overdue: 'Overdue',
  actions: 'Actions', actions_done: 'Actions done', actions_overdue: 'Actions overdue',
}
const TEXT_MEASURES = new Set(['track', 'exception', 'step', 'reason', 'risk_tier'])
export const NONE = '—'

export interface ExecTable { headers: string[]; rows: string[][]; numeric: boolean[]; csvHeaders: string[] }
const RISK: Record<string, string> = { VeryHigh: 'Very high' }

/** The table behind a grouped metric: a column per grouping the request asked for, then the measures. A group column the request did not ask for is dropped, so an ungrouped total is just its measures. */
export function execTable(m: Metric, groupBy: string[]): ExecTable {
  const keep = m.columns.filter(c => { const k = KEY_COLUMNS.find(x => x.column === c.name); return !k || groupBy.includes(k.dimension) })
  const headers = keep.map(c => KEY_COLUMNS.find(x => x.column === c.name)?.header ?? HEADERS[c.name] ?? c.name)
  const numeric = keep.map(c => !KEY_COLUMNS.some(x => x.column === c.name) && !TEXT_MEASURES.has(c.name))
  const cell = (v: unknown, name: string): string => {
    if (v == null || v === '') return NONE
    if (typeof v === 'number') return name.endsWith('_pct') ? `${fmt(v)}%` : fmt(v)
    return name === 'risk_tier' ? RISK[String(v)] ?? String(v) : String(v)
  }
  return { headers, numeric, csvHeaders: keep.map(c => c.name), rows: m.recs.map(r => keep.map(c => cell(r[c.name], c.name))) }
}

// ---- M17 -----------------------------------------------------------------------------------------------------------------------------
export interface Track { measured: number; onTime: number; completed: number; failed: number; pct: number | null; medianSlip: number | null }
/** CERT and Production are measured separately; every other type is "Other". Sums are additive over any grouping that does not overlap (not segment). */
export function trackTotals(recs: Rec[], track: 'CERT' | 'Production' | 'Other'): Track {
  const rs = recs.filter(r => r.track === track)
  const measured = sum(rs, 'measured'), onTime = sum(rs, 'on_time')
  return { measured, onTime, completed: sum(rs, 'completed'), failed: sum(rs, 'change_failed'), pct: pct(onTime, measured), medianSlip: rs.length === 1 && typeof rs[0].median_slip_days === 'number' ? rs[0].median_slip_days : null }
}

export interface QuarterPoint { label: string; CERT: number | null; Production: number | null }
/** On-time % per quarter for CERT and Production, from M17 grouped by quarter (rows of other groupings are summed first). Quarter labels sort as text (2026-Q2 < 2026-Q3, FY2027-Q1 < FY2027-Q2). */
export function onTimeByQuarter(recs: Rec[]): QuarterPoint[] {
  const quarters = [...new Set(recs.map(r => String(r.quarter ?? '')).filter(Boolean))].sort()
  return quarters.map(q => {
    const at = (t: string) => { const rs = recs.filter(r => r.quarter === q && r.track === t); return pct(sum(rs, 'on_time'), sum(rs, 'measured')) }
    return { label: q, CERT: at('CERT'), Production: at('Production') }
  })
}

export const onTimeFinding = (pts: QuarterPoint[]): string => {
  const withData = pts.filter(p => p.CERT !== null || p.Production !== null)
  if (withData.length === 0) return 'No release was measured against its baseline in this period'
  const last = withData[withData.length - 1]
  const part = (n: string, v: number | null) => `${n} ${v === null ? 'not measured' : `${fmt(v)}%`}`
  return `${last.label}: ${part('CERT', last.CERT)}, ${part('Production', last.Production)} on time`
}

// ---- headlines -----------------------------------------------------------------------------------------------------------------------
export interface ExecHeadline { key: string; label: string; value: string; sub: string; tone: 'ok' | 'warn' | 'bad' | 'none' }
const toneByShare = (p: number | null, ok: number, warn: number): ExecHeadline['tone'] => (p === null ? 'none' : p >= ok ? 'ok' : p >= warn ? 'warn' : 'bad')
const toneByCeiling = (p: number | null, ok: number, warn: number): ExecHeadline['tone'] => (p === null ? 'none' : p <= ok ? 'ok' : p <= warn ? 'warn' : 'bad')

/** The VP's six figures from the ungrouped (portfolio total) responses. A metric that did not load is a dash with "Not loaded", never a zero. */
export function vpHeadlines(t: { M17?: Rec[]; M16?: Rec[]; M18?: Rec[]; M21?: Rec[] }): ExecHeadline[] {
  const H = (key: string, label: string, value: string, sub: string, tone: ExecHeadline['tone'] = 'none'): ExecHeadline => ({ key, label, value, sub, tone })
  const track = (key: string, label: string, name: 'CERT' | 'Production') => {
    if (!t.M17) return H(key, label, NONE, 'Not loaded')
    const x = trackTotals(t.M17, name)
    return x.pct === null ? H(key, label, NONE, 'No release measured in this period') : H(key, label, `${fmt(x.pct)}%`, `${x.onTime} of ${x.measured} at or before baseline`, toneByShare(x.pct, 85, 70))
  }
  let cfail = H('change-fail', 'Change fail', NONE, t.M17 ? 'No release completed in this period' : 'Not loaded')
  if (t.M17) {
    const done = sum(t.M17, 'completed'), failed = sum(t.M17, 'change_failed')
    if (done > 0) cfail = H('change-fail', 'Change fail', `${fmt(pct(failed, done)!)}%`, `${failed} of ${done} not Successful`, toneByCeiling(pct(failed, done), 10, 20))
  }
  let restore = H('restore', 'Time to restore', NONE, t.M16 ? 'No rolled-back run in this period' : 'Not loaded')
  if (t.M16 && t.M16.length > 0 && typeof t.M16[0].median_min === 'number') restore = H('restore', 'Time to restore', `${fmt(t.M16[0].median_min)} min`, `median of ${num(t.M16[0].restores)} rolled-back ${num(t.M16[0].restores) === 1 ? 'run' : 'runs'}`, 'warn')
  const exc = t.M18 ? sum(t.M18, 'events') : null
  const open = t.M21 ? sum(t.M21, 'open_releases') : null
  const noReh = t.M21 ? sum(t.M21, 'no_rehearsal') : 0
  return [track('on-time-cert', 'On time, CERT', 'CERT'), track('on-time-production', 'On time, Production', 'Production'), cfail, restore,
    exc === null ? H('exceptions', 'Exceptions', NONE, 'Not loaded') : H('exceptions', 'Exceptions', String(exc), exc === 0 ? 'none in this period' : 'waivers, overrides, late and open items', exc === 0 ? 'ok' : 'warn'),
    open === null ? H('open', 'Open releases', NONE, 'Not loaded') : H('open', 'Open releases', String(open), `${noReh} High or Very high without a rehearsed rollback · as of now`, noReh > 0 ? 'warn' : 'none')]
}

/** The Director's five figures from the ungrouped responses (M22, M9 blockers, M20 moves, M24 reviews). */
export function directorHeadlines(t: { M22?: Rec[]; M9?: Rec[]; M20?: Rec[]; M24?: Rec[]; M19?: Rec[] }): ExecHeadline[] {
  const H = (key: string, label: string, value: string, sub: string, tone: ExecHeadline['tone'] = 'none'): ExecHeadline => ({ key, label, value, sub, tone })
  const done = t.M22 ? sum(t.M22, 'completed') : null
  const lead = t.M22 && t.M22.length === 1 && typeof t.M22[0].median_lead_days === 'number' ? t.M22[0].median_lead_days : null
  const blockers = t.M9 ? t.M9.reduce((a, r) => a + num(r.lt_1d) + num(r.d1_3) + num(r.d3_7) + num(r.ge_7d), 0) : null
  const old = t.M9 ? t.M9.reduce((a, r) => a + num(r.ge_7d), 0) : 0
  const moves = t.M20 ? sum(t.M20, 'moves') : null
  const unrecorded = t.M20 ? sum(t.M20.filter(r => r.reason === 'Not recorded'), 'moves') : 0
  const overdue = t.M24 ? sum(t.M24, 'overdue') + sum(t.M24, 'actions_overdue') : null
  return [
    done === null ? H('completed', 'Releases completed', NONE, 'Not loaded') : H('completed', 'Releases completed', String(done), done === 0 ? 'none in this period' : 'in the period'),
    lead === null ? H('lead', 'Median lead time', NONE, t.M22 ? 'No release completed in this period' : 'Not loaded') : H('lead', 'Median lead time', `${fmt(lead)} d`, 'created to complete'),
    blockers === null ? H('blockers', 'Open blockers', NONE, 'Not loaded') : H('blockers', 'Open blockers', String(blockers), old > 0 ? `${old} older than 7 days · as of now` : 'none older than 7 days · as of now', old > 0 ? 'bad' : 'none'),
    moves === null ? H('moves', 'Target moves', NONE, 'Not loaded') : H('moves', 'Target moves', String(moves), moves === 0 ? 'none after a baseline' : `${unrecorded} with no recorded reason`, moves > 0 ? 'warn' : 'ok'),
    overdue === null ? H('overdue', 'Review follow-up overdue', NONE, 'Not loaded') : H('overdue', 'Review follow-up overdue', String(overdue), 'reviews and review actions', overdue > 0 ? 'bad' : 'ok'),
  ]
}
