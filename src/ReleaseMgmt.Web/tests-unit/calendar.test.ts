// REOS-96 / REOS-134: the pure parts of the Portfolio & calendar screen: UTC weeks and months, the filters, the entries of a day, conflicts, durations and the
// UTC-with-local stamp (D44).
import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  addDays, addMonths, applyFilters, conflictsOf, dayLabel, durationText, entriesByDay, freezeDays, NO_FILTER, rangeTitle, releaseRows, riskLabel, stamp, startOfWeek, utcDay, utcHM, visibleDays,
  type CalendarPayload,
} from '../src/calendarModel.ts'

const payload = (over: Partial<CalendarPayload> = {}): CalendarPayload => ({
  from: '2026-10-18', to: '2026-10-26',
  trains: [
    { id: 't1', title: 'Checkout resilience', status: 'Executing', riskTier: 'High', targetDate: '2026-10-23', window: { startsAt: '2026-10-23T14:00:00Z', endsAt: '2026-10-23T16:00:00Z' }, collisions: [] },
    { id: 't2', title: 'Identity federation', status: 'Gated', riskTier: 'Moderate', targetDate: '2026-10-23', window: { startsAt: '2026-10-23T15:00:00Z', endsAt: '2026-10-23T17:30:00Z' }, collisions: [{ trainId: 't1', title: 'Checkout resilience', shared: ['CI payments-api'] }] },
    { id: 't3', title: 'Old release', status: 'Complete', riskTier: 'Low', targetDate: '2026-10-20', window: null },
  ],
  gates: [{ id: 'g1', name: 'Code Freeze', class: 'Standard', status: 'InProgress', dueOn: '2026-10-21', trainId: 't1', trainTitle: 'Checkout resilience', trainStatus: 'Executing' }],
  freezeWindows: [{ id: 'f1', name: 'Q4 close', kind: 'Freeze', startsAt: '2026-10-23T00:00:00Z', endsAt: '2026-10-26T00:00:00Z', scope: 'All products' }],
  milestones: [{ id: 'm1', name: 'Business sign-off', dueOn: '2026-10-19', done: false, trainId: 't2', trainTitle: 'Identity federation', trainStatus: 'Gated' }],
  ...over,
})

test('a week runs Monday to Sunday and a month covers whole weeks; dates are UTC calendar days', () => {
  assert.equal(startOfWeek('2026-10-23'), '2026-10-19')
  assert.deepEqual(visibleDays('2026-10-23', 'week'), ['2026-10-19', '2026-10-20', '2026-10-21', '2026-10-22', '2026-10-23', '2026-10-24', '2026-10-25'])
  const m = visibleDays('2026-10-23', 'month')
  assert.equal(m.length % 7, 0)
  assert.equal(m[0], '2026-09-28')
  assert.equal(m[m.length - 1], '2026-11-01')
  assert.equal(addMonths('2026-01-31', 1), '2026-02-28')
  assert.equal(addDays('2026-12-31', 1), '2027-01-01')
  assert.equal(utcDay('2026-10-23T23:30:00-05:00'), '2026-10-24')   // an instant belongs to the UTC day, whatever offset it was written with
  assert.equal(utcHM('2026-10-23T14:05:00Z'), '14:05')
})

test('the heading names the range as the design does', () => {
  assert.equal(rangeTitle('2026-10-23', 'week'), '19–25 October 2026')
  assert.equal(rangeTitle('2026-10-30', 'week'), '26 October – 1 November 2026')
  assert.equal(rangeTitle('2026-10-23', 'month'), 'October 2026')
  assert.equal(dayLabel('2026-10-03'), '3 Oct')
})

test('entries: a window sits on its UTC day with its range in UTC, the target, gates and milestones on their dates, each with a status word', () => {
  const m = entriesByDay(payload(), '2026-10-23')
  const day = m.get('2026-10-23')!
  const w = day.find(e => e.key === 'w:t1')!
  assert.equal(w.detail, '14:00–16:00 UTC')
  assert.equal(w.label, 'Executing'); assert.equal(w.tone, 'info')
  assert.ok(day.some(e => e.key === 't:t1' && e.detail === 'Target date'))
  const g = m.get('2026-10-21')!.find(e => e.kind === 'gate')!
  assert.equal(g.label, 'In progress'); assert.equal(g.tone, 'info')
  const ms = m.get('2026-10-19')!.find(e => e.kind === 'milestone')!
  assert.equal(ms.label, 'Overdue'); assert.equal(ms.tone, 'danger'); assert.match(ms.name, /^◆ /)
  assert.equal(entriesByDay(payload(), '2026-10-01').get('2026-10-19')!.find(e => e.kind === 'milestone')!.label, 'Open')
})

test('conflicts: a shared-resource overlap and a freeze overlap are flagged with words; a finished release has none', () => {
  const c = conflictsOf(payload())
  assert.deepEqual(c.map(x => [x.trainId, x.kind]).sort(), [['t1', 'freeze'], ['t2', 'freeze'], ['t2', 'overlap']].sort())
  assert.match(c.find(x => x.kind === 'overlap')!.text, /Identity federation overlaps Checkout resilience \(CI payments-api\)/)
  assert.match(c.find(x => x.kind === 'freeze')!.text, /overlaps the freeze Q4 close \(All products, 2026-10-23 to 2026-10-25\)/)
  assert.ok(!c.some(x => x.trainId === 't3'))
  const chill = payload({ freezeWindows: [{ id: 'f2', name: 'Holiday chill', kind: 'Chill', startsAt: '2026-10-23T00:00:00Z', endsAt: '2026-10-24T00:00:00Z', scope: 'All products' }] })
  assert.ok(conflictsOf(chill).every(x => x.kind === 'chill' || x.kind === 'overlap'))
  const ends = payload({ freezeWindows: [{ id: 'f3', name: 'Edge', kind: 'Freeze', startsAt: '2026-10-22T00:00:00Z', endsAt: '2026-10-23T14:00:00Z', scope: 'All products' }] })
  assert.ok(!conflictsOf(ends).some(x => x.kind === 'freeze'), 'a freeze that ends as the window opens does not overlap it')
  const flagged = entriesByDay(payload(), '2026-10-23').get('2026-10-23')!.find(e => e.key === 'w:t2')!
  assert.match(flagged.conflict!, /overlaps/)
})

test('the filters narrow trains and the gates and milestones that belong to them together; freezes stay', () => {
  const d = payload()
  const byStatus = applyFilters(d, { ...NO_FILTER, status: 'Gated' })
  assert.deepEqual(byStatus.trains.map(t => t.id), ['t2'])
  assert.deepEqual(byStatus.gates, [])
  assert.deepEqual(byStatus.milestones!.map(m => m.id), ['m1'])
  assert.equal(byStatus.freezeWindows.length, 1)
  const byRisk = applyFilters(d, { ...NO_FILTER, risk: 'High' })
  assert.deepEqual(byRisk.trains.map(t => t.id), ['t1'])
  assert.deepEqual(byRisk.gates.map(g => g.id), ['g1'])
  assert.deepEqual(byRisk.milestones, [])
  assert.equal(applyFilters(d, NO_FILTER), d)
  assert.deepEqual(applyFilters(d, { status: 'Executing', risk: 'Low' }).trains, [])
})

test('the week grid has one row per release with something on a visible day, in order of its first item', () => {
  const d = payload()
  const days = visibleDays('2026-10-23', 'week')
  const rows = releaseRows(d, entriesByDay(d, '2026-10-23'), days)
  assert.deepEqual(rows.map(r => r.id), ['t2', 't3', 't1'])
  // a release known only by a gate due this week still gets a row
  const gateOnly = payload({ trains: [], milestones: [] })
  assert.deepEqual(releaseRows(gateOnly, entriesByDay(gateOnly, '2026-10-23'), days).map(r => [r.id, r.status]), [['t1', 'Executing']])
})

test('freeze days are the UTC days a freeze covers, the end instant excluded', () => {
  const m = freezeDays(payload(), '2026-10-31')
  assert.deepEqual([...m.keys()], ['2026-10-23', '2026-10-24', '2026-10-25'])
})

test('durations, risk names and the UTC stamp with the person\'s time beside it (D44)', () => {
  assert.equal(durationText('2026-10-23T14:00:00Z', '2026-10-23T16:00:00Z'), '2h window')
  assert.equal(durationText('2026-10-23T16:00:00Z', '2026-10-23T17:30:00Z'), '1h 30m window')
  assert.equal(durationText('2026-10-23T16:00:00Z', '2026-10-23T16:45:00Z'), '45m window')
  assert.equal(riskLabel('VeryHigh'), 'Very high')
  assert.deepEqual(stamp('2026-10-23T14:00:00Z', 'America/Chicago'), { utc: '23 Oct 14:00 UTC', local: '09:00 CDT' })
  assert.deepEqual(stamp('2026-10-23T14:00:00Z', 'UTC'), { utc: '23 Oct 14:00 UTC', local: null })
})
