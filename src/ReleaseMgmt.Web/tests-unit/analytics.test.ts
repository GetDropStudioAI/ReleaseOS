// REOS-47 unit tests: `npm test` (node --test, TypeScript stripped by Node; no extra tooling). Pure logic only: findings, headlines, toCsv, svg title.
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import { aggregateSteps, chartTable, columnHeader, findingM2, findingM3, findingM4, findingM5, findingM6, findingM7, findingM8, findingM9, findingM10, findingM11, findingM13, findingM14, findingM15,
  fmt, headlines, monthLabel, normalize, NO_DATA, type Metric, type MetricResponse } from '../src/analyticsFindings.ts'
import { csvCell, exportName, svgWithTitle, toCsv } from '../src/analyticsExport.ts'

// The oracle fixture (real data shapes): columns and rows per metric, SQL column names.
const fx = JSON.parse(readFileSync(new URL('../../../tests/reference/fixtures/expected_metrics.json', import.meta.url), 'utf8')) as { metrics: Record<string, { key: string; columns: string[]; rows: unknown[][] }> }
/** Builds a Metric as the API would return it: row objects keyed by odd camelCased names (positions matter, not names). */
function metric(name: string): Metric {
  const m = fx.metrics[name]
  const r: MetricResponse = { metric: m.key, name, title: name, unit: '', from: '2026-01-01', to: '2026-12-31', asOf: '2026-09-28T12:00:00Z',
    columns: m.columns.map(c => ({ name: c, unit: '' })), rows: m.rows.map(row => Object.fromEntries(row.map((v, i) => [`k${i}x`, v]))) }
  return normalize(r)
}
const recs = (n: string) => metric(n).recs

test('normalize reads rows by position and keys them by SQL column name; null stays null', () => {
  const m = metric('freeze_exceptions')
  assert.deepEqual(m.recs[0], { Name: 'Q3 close', overrides: 0, avg_ttl_h: null })
  assert.equal(m.cells[0].length, 3)
})

test('findings on the seeded data', () => {
  assert.equal(findingM2(recs('slip_days')), '6 of 22 trains shipped late, each by 1 or 5 days')
  assert.equal(findingM3(recs('gate_cycle_time')), 'QA Sign-off is the slowest gate: median 5.4 days, p90 9.4')
  assert.match(findingM4(recs('gate_first_pass')), /^CAB Approval has the lowest first-pass rate: 90.9%$/)
  assert.equal(findingM5(recs('gate_late')), 'QA Sign-off is certified late on more than half of trains')
  assert.match(findingM6(recs('scope_churn')), /^\d+ of 24 trains changed scope after Code Freeze|^No train changed scope/)
  assert.match(findingM7(recs('runbook_variance')), /^R-\d{3} runs long most: [\d.]+ min over on average, [\d.]+ at worst$/)
  assert.match(findingM8(recs('runbook_summary')), /^R26\.\d\d overran most: /)
  assert.match(findingM9(recs('blocker_aging')), /^(One|\d+) (Critical|High|Medium|Low) blockers? (has|have) been open 7 days or more, the oldest \d+ days$/)
  assert.equal(findingM10(recs('outcomes')), '3 of 22 trains closed with issues or unsuccessfully; 9.1% rolled back')
  assert.equal(findingM11(recs('freeze_exceptions')), 'No freeze was overridden in this window')
  assert.match(findingM13(recs('waiver_rate')), /^3 of 44 Compliance gates were waived \(6\.8%\); peak /)
  assert.match(findingM14(recs('sync_health')), /^\d+ of \d+ external links are mismatched or broken$/)
  assert.match(findingM15(recs('throughput')), /^Throughput peaked at \d+ trains in [A-Z][a-z]{2} 2026; median lead time [\d.]+ days that month$/)
})

test('the M9 finding follows the mockup: highest severity with a 7 d+ blocker', () => {
  assert.equal(findingM9([{ Severity: 'Critical', lt_1d: 1, d1_3: 0, d3_7: 0, ge_7d: 0, oldest_days: 0.4 }, { Severity: 'High', lt_1d: 0, d1_3: 1, d3_7: 0, ge_7d: 1, oldest_days: 8.0 },
    { Severity: 'Low', lt_1d: 0, d1_3: 0, d3_7: 0, ge_7d: 1, oldest_days: 12.0 }]), 'One High blocker has been open 7 days or more, the oldest 8 days')
  assert.equal(findingM9([{ Severity: 'Low', lt_1d: 2, d1_3: 0, d3_7: 0, ge_7d: 0, oldest_days: 0.5 }]), '2 open blockers, none older than 7 days')
  assert.equal(findingM9([{ Severity: 'Low', lt_1d: 0, d1_3: 0, d3_7: 0, ge_7d: 0, oldest_days: null }]), 'No open blockers')
})

test('every finding gives an honest empty state, never a blank title', () => {
  for (const f of [findingM2, findingM3, findingM4, findingM5, findingM6, findingM7, findingM8, findingM9, findingM10, findingM11, findingM13, findingM14, findingM15]) assert.equal(f([]), NO_DATA, f.name)
  assert.equal(findingM10([{ completed: 0, successful: null, with_issues: null, unsuccessful: null, rollback_pct: null, change_fail_pct: null }]), NO_DATA)
  assert.equal(findingM13([{ month: null, compliance_gates: 0, waived: null, waived_pct: null }]), NO_DATA)
})

test('findings for the calm cases', () => {
  assert.equal(findingM2([{ slip_days: 0 }, { slip_days: 0 }]), 'All 2 trains shipped on or before their baseline')
  assert.equal(findingM2([{ slip_days: 3 }, { slip_days: 0 }]), '1 of 2 trains shipped late, by 3 days')
  assert.equal(findingM2([{ slip_days: 1 }, { slip_days: 2 }, { slip_days: 3 }, { slip_days: 9 }]), '4 of 4 trains shipped late, each by up to 9 days')
  assert.equal(findingM5([{ GateName: 'A', gates: 10, late: 0, avg_days_late: 0 }]), 'No gate was certified late')
  assert.equal(findingM5([{ GateName: 'A', gates: 10, late: 3, avg_days_late: 1 }]), 'A is certified late most often: 3 of 10 (30%)')
  assert.equal(findingM4([{ GateName: 'A', gates: 3, first_pass_pct: 100, waived: 0 }]), 'Every gate passed first time (100%)')
  assert.equal(findingM11([{ Name: 'Q3', overrides: 2, avg_ttl_h: 4 }, { Name: 'Q4', overrides: 1, avg_ttl_h: 1 }]), '3 freeze overrides; Q3 most (2)')
  assert.equal(findingM14([{ SourceSystem: 'Jira', links: 4, in_sync: 4, mismatch: 0, broken: 0, stalest_min: 1, open_alerts: 0 }]), 'All 4 external links are in sync')
  assert.equal(findingM7([{ StepCode: 'R-001', Section: 'x', overrun_min: 0 }]), 'No runbook step ran over its plan')
})

test('aggregateSteps averages per step code and keeps the worst run', () => {
  const a = aggregateSteps([{ StepCode: 'R-002', Section: 's', overrun_min: 2 }, { StepCode: 'R-001', Section: 's', overrun_min: 1 }, { StepCode: 'R-001', Section: 's', overrun_min: 4 }, { StepCode: 'R-003', Section: 's', overrun_min: null }])
  assert.deepEqual(a.map(x => [x.step, x.runs, x.avg, x.worst]), [['R-001', 2, 2.5, 4], ['R-002', 1, 2, 2]])
})

test('six headline figures from the seeded data match the mockup copy', () => {
  const h = headlines({ M1: recs('on_time_rate'), M2: recs('slip_days'), M10: recs('outcomes'), M12: recs('comms_timeliness'), M13: recs('waiver_rate') })
  assert.deepEqual(h.map(k => [k.label, k.value, k.sub]), [
    ['On time', '72.7%', '16 of 22 at or before baseline'],
    ['Slip when late', '5 d', 'median of 6 late trains'],
    ['Change fail', '13.6%', '3 of 22 not Successful'],
    ['Rollback', '9.1%', '2 of 22 trains rolled back'],
    ['Comms on time', '81.8%', '54 of 66 scheduled sends'],
    ['Waived gates', '3', 'of 44 Compliance gates · 6.8%'],
  ])
})

test('headline figures with no data or a failed metric show a dash and a reason, not zero', () => {
  const none = headlines({ M1: [], M2: [], M10: [], M12: [], M13: [] })
  assert.equal(none.length, 6)
  assert.ok(none.every(k => k.value === '—' && k.sub === NO_DATA), JSON.stringify(none))
  const missing = headlines({})
  assert.ok(missing.every(k => k.value === '—' && k.sub === 'Not loaded'))
})

test('chartTable: M7 is aggregated, others carry the metric columns with non-empty headers', () => {
  const t7 = chartTable('M7', recs('runbook_variance'), metric('runbook_variance').columns)
  assert.ok(t7.rows.length > 0 && t7.rows.length < 50)
  const m = metric('gate_cycle_time')
  const t3 = chartTable('M3', m.recs, [{ name: 'GateName', unit: '' }, { name: 'samples', unit: 'gates' }, { name: 'median_h', unit: 'hours' }, { name: 'p90_h', unit: 'hours' }])
  assert.ok(t3.headers.every(h => h.length > 0))
  assert.deepEqual(t3.headers, ['GateName', 'samples (gates)', 'median_h (hours)', 'p90_h (hours)'])
  assert.deepEqual(t3.rows[0], ['QA Sign-off', '24', '130', '226'])
  assert.equal(columnHeader({ name: 'x', unit: '' }), 'x')
})

test('fmt and monthLabel', () => {
  assert.equal(fmt(5), '5'); assert.equal(fmt(5.42), '5.4'); assert.equal(fmt(72.65, 1), '72.7'.replace('72.7', String(Number((72.65).toFixed(1)))))
  assert.equal(monthLabel('2026-05'), 'May 2026'); assert.equal(monthLabel('nope'), 'nope')
})

test('toCsv: RFC 4180 quoting, CRLF, header row, nulls empty', () => {
  assert.equal(toCsv([['a,b', 'say "hi"', 'line\nbreak', null, 3, 1.5]], ['c1', 'c2', 'c3', 'c4', 'c5', 'c6']),
    'c1,c2,c3,c4,c5,c6\r\n"a,b","say ""hi""","line\nbreak",,3,1.5\r\n')
  assert.equal(toCsv([], ['x']), 'x\r\n')
  assert.equal(toCsv([[1]], ['x'], { bom: true }).charCodeAt(0), 0xfeff)
})

test('toCsv: OWASP CSV injection, text cells starting = + - @ TAB CR are prefixed with an apostrophe; numbers are not touched', () => {
  for (const evil of ['=1+1', '+1', '-1+2', '@SUM(A1)', '\tcmd', '\rcmd', '=HYPERLINK("http://x","y")']) {
    const c = csvCell(evil)
    assert.ok(c.replace(/^"/, '').startsWith("'"), `${JSON.stringify(evil)} -> ${c}`)
  }
  assert.equal(csvCell(-3), '-3')
  assert.equal(csvCell('safe'), 'safe')
  assert.equal(csvCell('=A,B'), `"'=A,B"`)
  assert.equal(toCsv([['=cmd|calc']], ['=h']), "'=h\r\n'=cmd|calc\r\n")
})

test('svgWithTitle puts the finding first and escapes it; exportName is filesystem-safe', () => {
  const out = svgWithTitle('<svg width="10" height="5"><g/></svg>', 'A < B & "C"')
  assert.equal(out, '<svg width="10" height="5" role="img"><title>A &lt; B &amp; &quot;C&quot;</title><g/></svg>')
  assert.equal(exportName('M2', '2026-04-01', '2026-09-28', 'csv'), 'analytics-m2-2026-04-01_2026-09-28.csv')
  assert.equal(exportName('M9', '2026-04-01', '2026-09-28', 'xlsx', true), 'analytics-m9-asof-2026-09-28.xlsx')
})
