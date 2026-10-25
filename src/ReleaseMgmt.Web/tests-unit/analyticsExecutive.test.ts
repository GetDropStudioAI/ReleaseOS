import { test } from 'node:test'
import assert from 'node:assert/strict'
import { chosenWorkspaces, defaultChoice, defaultGroup, directorHeadlines, execQuery, execTable, groupingsFor, onTimeByQuarter, onTimeFinding, parseView, portfolioPdfName, portfolioPdfUrl, quarterBasis,
  scopeHeading, trackTotals, vpHeadlines, workspacesParam, type Scope } from '../src/analyticsExecutive.ts'
import { normalize } from '../src/analyticsFindings.ts'
import { BUILDERS, type Palette } from '../src/analyticsCharts.ts'

const scope: Scope = { workspaces: [
  { id: 'a', slug: 'cards', name: 'Cards', role: 'RTE', current: true }, { id: 'b', slug: 'payments', name: 'Payments', role: 'Viewer', current: false }, { id: 'c', slug: 'default', name: 'Default', role: 'Viewer', current: false }],
dimensions: [], fiscalYearStartMonth: 1, quarters: 'calendar' }

test('views and their defaults: Operations reads the current workspace, the executive views read all of the person\'s', () => {
  assert.equal(parseView('vp'), 'vp'); assert.equal(parseView('director'), 'director'); assert.equal(parseView('x'), 'ops'); assert.equal(parseView(null), 'ops')
  assert.equal(defaultChoice('ops'), ''); assert.equal(defaultChoice('vp'), 'all'); assert.equal(defaultChoice('director'), 'all')
  assert.equal(defaultGroup('vp'), 'workspace,quarter'); assert.equal(defaultGroup('director'), 'team,type'); assert.equal(defaultGroup('ops'), '')
  assert.ok(groupingsFor('vp').some(g => g.value === defaultGroup('vp'))); assert.ok(groupingsFor('director').some(g => g.value === defaultGroup('director')))
})

test('execQuery: period, workspaces, groupBy only for grouped metrics, release type', () => {
  assert.equal(execQuery({ choice: '' }), '')
  assert.equal(execQuery({ from: '2026-01-01', to: '2026-06-30', choice: 'all', groupBy: 'workspace,quarter', grouped: true }), '?from=2026-01-01&to=2026-06-30&workspaces=all&groupBy=workspace%2Cquarter')
  assert.equal(execQuery({ choice: 'cards', groupBy: 'team', grouped: false }), '?workspaces=cards')   // M1..M15 and M3/M9 never carry groupBy (400)
  assert.equal(execQuery({ choice: 'all', groupBy: '', grouped: true }), '?workspaces=all')            // no grouping = the portfolio total
  assert.equal(execQuery({ choice: '', type: 't 1', now: '2026-09-28T12:00:00Z' }), '?now=2026-09-28T12%3A00%3A00Z&releaseTypeId=t%201')
  assert.equal(workspacesParam(''), ''); assert.equal(workspacesParam('a b'), 'workspaces=a%20b')
})

test('the portfolio report asks for all of the person\'s workspaces unless one is chosen', () => {
  assert.equal(portfolioPdfUrl({ from: '2026-01-01', to: '2026-03-31', now: '2026-04-01T00:00:00Z', choice: '' }), '/api/v1/analytics/portfolio.pdf?from=2026-01-01&to=2026-03-31&now=2026-04-01T00%3A00%3A00Z&workspaces=all')
  assert.ok(portfolioPdfUrl({ from: 'a', to: 'b', now: 'n', choice: 'cards' }).endsWith('workspaces=cards'))
  assert.equal(portfolioPdfName('2026-01-01', '2026-03-31'), 'portfolio-report-2026-01-01_2026-03-31.pdf')
})

test('the heading names only the workspaces read, and how many of the person\'s own', () => {
  assert.equal(scopeHeading(chosenWorkspaces(scope, 'all').slice(0, 2), 3), 'Cards, Payments (2 of 3 of your workspaces)')
  assert.deepEqual(chosenWorkspaces(scope, '').map(w => w.slug), ['cards'])
  assert.deepEqual(chosenWorkspaces(scope, 'payments').map(w => w.slug), ['payments'])
  assert.deepEqual(chosenWorkspaces(scope, 'someone-elses'), [])   // a workspace the person is not in is never offered, and never shown
  assert.equal(scopeHeading([], 0), 'No workspace (0 of 0 of your workspaces)')
})

test('quarter basis in words', () => {
  assert.match(quarterBasis({ quarters: 'calendar', fiscalYearStartMonth: 1 }), /^Calendar quarters/)
  assert.match(quarterBasis({ quarters: 'fiscal', fiscalYearStartMonth: 10 }), /^Fiscal quarters, the year starts in month 10/)
})

const M17 = [
  { quarter: '2026-Q2', track: 'CERT', completed: 4, measured: 4, on_time: 3, change_failed: 1, median_slip_days: 0 },
  { quarter: '2026-Q2', track: 'Production', completed: 3, measured: 3, on_time: 3, change_failed: 0, median_slip_days: 1 },
  { quarter: '2026-Q3', track: 'CERT', completed: 2, measured: 1, on_time: 0, change_failed: 0, median_slip_days: 5 },
  { quarter: '2026-Q3', track: 'Other', completed: 1, measured: 0, on_time: null, change_failed: 1, median_slip_days: null },
]

test('on time is measured separately for CERT and Production', () => {
  const cert = trackTotals(M17, 'CERT'), prod = trackTotals(M17, 'Production')
  assert.deepEqual([cert.measured, cert.onTime, cert.pct, cert.failed], [5, 3, 60, 1])
  assert.deepEqual([prod.measured, prod.onTime, prod.pct], [3, 3, 100])
  assert.equal(trackTotals(M17, 'Other').pct, null)   // nothing measured is not 0 %
})

test('on time by quarter: one point per quarter, a track with nothing measured is null', () => {
  const pts = onTimeByQuarter([...M17].reverse())
  assert.deepEqual(pts, [{ label: '2026-Q2', CERT: 75, Production: 100 }, { label: '2026-Q3', CERT: 0, Production: null }])
  assert.equal(onTimeFinding(pts), '2026-Q3: CERT 0%, Production not measured on time')
  assert.equal(onTimeFinding([]), 'No release was measured against its baseline in this period')
})

test('the on-time chart builder draws one bar series per track from those points', () => {
  const p = { bg: '#fff', label: '#000', secondary: '#111', tertiary: '#222', grid: '#333', rule: '#444', accent: '#555', ok: '#666', warn: '#777', bad: '#888', sans: 'sans', mono: 'mono' } as Palette
  const o = BUILDERS.VP_ONTIME(onTimeByQuarter(M17) as unknown as Record<string, unknown>[], p) as { series: { name: string; data: (number | null)[] }[]; xAxis: { data: string[] } }
  assert.deepEqual(o.xAxis.data, ['2026-Q2', '2026-Q3'])
  assert.deepEqual(o.series.map(s => [s.name, s.data]), [['CERT', [75, 0]], ['Production', [100, null]]])
})

test('the table keeps only the group columns that were asked for, with words for headers', () => {
  const m = normalize({ metric: 'M18', name: 'exceptions_summary', title: 'Risk and compliance exceptions', unit: 'events', from: '2026-01-01', to: '2026-12-31', asOf: '2026-09-28T12:00:00Z',
    columns: ['workspace', 'team', 'release_type', 'version', 'segment', 'quarter', 'exception', 'events', 'releases'].map(name => ({ name, unit: '' })),
    rows: [{ workspace: 'Cards', team: '', releaseType: '', version: '', segment: '', quarter: '2026-Q2', exception: 'Waiver granted', events: 2, releases: 2 }] })
  const t = execTable(m, ['workspace', 'quarter'])
  assert.deepEqual(t.headers, ['Workspace', 'Quarter', 'Exception', 'Events', 'Releases'])
  assert.deepEqual(t.rows, [['Cards', '2026-Q2', 'Waiver granted', '2', '2']])
  assert.deepEqual(t.numeric, [false, false, false, true, true])
  assert.deepEqual(execTable(m, []).headers, ['Exception', 'Events', 'Releases'])   // a total has no group columns
})

test('percent and empty cells in the table', () => {
  const m = normalize({ metric: 'M17', name: 'predictability', title: 't', unit: '%', from: 'a', to: 'b', asOf: 'c', columns: [{ name: 'track', unit: '' }, { name: 'on_time_pct', unit: '%' }, { name: 'median_slip_days', unit: 'days' }], rows: [{ track: 'CERT', onTimePct: 66.7, medianSlipDays: null }] })
  assert.deepEqual(execTable(m, []).rows, [['CERT', '66.7%', '—']])
})

test('VP headlines: CERT and Production separately, change failure, restore, exceptions and open releases; a missing metric is a dash, not zero', () => {
  const h = vpHeadlines({ M17, M16: [{ restores: 2, median_min: 31 }], M18: [{ events: 3 }, { events: 2 }], M21: [{ open_releases: 4, no_rehearsal: 1 }, { open_releases: 2, no_rehearsal: 0 }] })
  assert.deepEqual(h.map(x => [x.label, x.value]), [['On time, CERT', '60%'], ['On time, Production', '100%'], ['Change fail', '20%'], ['Time to restore', '31 min'], ['Exceptions', '5'], ['Open releases', '6']])
  assert.equal(h[0].tone, 'bad'); assert.equal(h[1].tone, 'ok')
  assert.match(h[5].sub, /1 High or Very high without a rehearsed rollback/)
  const none = vpHeadlines({})
  assert.ok(none.every(x => x.value === '—' && x.sub === 'Not loaded'), JSON.stringify(none))
  const empty = vpHeadlines({ M17: [], M16: [], M18: [], M21: [] })
  assert.equal(empty[0].value, '—'); assert.equal(empty[3].sub, 'No rolled-back run in this period'); assert.equal(empty[4].value, '0')
})

test('Director headlines', () => {
  const h = directorHeadlines({ M22: [{ completed: 12, median_lead_days: 24.2 }], M9: [{ lt_1d: 1, d1_3: 0, d3_7: 2, ge_7d: 1 }], M20: [{ reason: 'No-Go decision', moves: 1 }, { reason: 'Not recorded', moves: 2 }], M24: [{ overdue: 1, actions_overdue: 2 }] })
  assert.deepEqual(h.map(x => [x.label, x.value]), [['Releases completed', '12'], ['Median lead time', '24.2 d'], ['Open blockers', '4'], ['Target moves', '3'], ['Review follow-up overdue', '3']])
  assert.match(h[2].sub, /1 older than 7 days/); assert.match(h[3].sub, /2 with no recorded reason/)
  assert.ok(directorHeadlines({}).every(x => x.value === '—'))
})
