// REOS-96 / REOS-134: the pure parts of the Portfolio & calendar screen: UTC weeks and months, the filters, the entries of a day, conflicts, durations and the
// UTC-with-local stamp (D44).
import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  addDays, addMonths, applyFilters, conflictsOf, dayLabel, durationText, entriesByDay, filtered, freezeDays, NO_FILTER, rangeTitle, releaseRows, riskLabel, stamp, startOfWeek, utcDay, utcHM, visibleDays,
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

// REOS-120 (RT5, D46 to D49): release types on the rows and cards, client dates as entries, and the type and client-segment filters.
const clientPayload = (): CalendarPayload => ({
  from: '2027-03-01', to: '2027-03-14',
  trains: [
    { id: 'prod', title: 'Orion Production', status: 'Planning', riskTier: 'Low', targetDate: '2027-03-05', window: null, type: { id: 'ty-prod', name: 'Production' }, clientKind: 'Production', segments: ['Standard', 'OnDemand'] },
    { id: 'cert', title: 'Orion CERT', status: 'Gated', riskTier: 'Low', targetDate: '2027-03-01', window: null, type: { id: 'ty-cert', name: 'CERT' }, clientKind: 'Cert', segments: ['OnDemand'] },
    { id: 'beta', title: 'Orion Beta 2', status: 'Executing', riskTier: 'Low', targetDate: '2027-03-03', window: null, type: { id: 'ty-beta', name: 'Beta' }, betaNumber: 2, clientKind: null, segments: [] },
  ],
  gates: [{ id: 'g1', name: 'Code Freeze', class: 'Standard', status: 'Pending', dueOn: '2027-03-02', trainId: 'cert', trainTitle: 'Orion CERT', trainStatus: 'Gated' }],
  freezeWindows: [],
  clientDates: [
    { trainId: 'cert', trainTitle: 'Orion CERT', trainStatus: 'Gated', kind: 'Cert', segment: 'OnDemand', date: '2027-03-08', wave: 'On-Demand', count: 1, clients: ['Globex'] },
    { trainId: 'prod', trainTitle: 'Orion Production', trainStatus: 'Planning', kind: 'Production', segment: 'Standard', date: '2027-03-05', wave: 'Standard', count: 2, clients: ['Acme', 'Zeta'] },
    { trainId: 'prod', trainTitle: 'Orion Production', trainStatus: 'Planning', kind: 'Production', segment: 'OnDemand', date: '2027-03-12', wave: 'On-Demand', count: 3, clients: ['Globex', 'Initech', 'Umbrella'] },
  ],
})

test('a target date names the release type, a numbered Beta its number; a release with no type says nothing extra', () => {
  const day = entriesByDay(clientPayload(), '2027-03-01')
  assert.equal(day.get('2027-03-03')!.find(e => e.kind === 'target')!.detail, 'Target date · Beta 2')
  assert.equal(day.get('2027-03-05')!.find(e => e.kind === 'target')!.detail, 'Target date · Production')
  assert.ok(day.get('2027-03-05')!.find(e => e.kind === 'target')!.title.includes('Production'))
  const untyped = clientPayload(); untyped.trains[0].type = null
  assert.equal(entriesByDay(untyped, '2027-03-01').get('2027-03-05')!.find(e => e.kind === 'target')!.detail, 'Target date')
})

test('client dates are entries on their day: the segment is a word in the chip, the clients are named, an On-Demand day on the Standard date says so', () => {
  const day = entriesByDay(clientPayload(), '2027-03-01')
  const std = day.get('2027-03-05')!.find(e => e.kind === 'client')!
  assert.equal(std.label, 'Standard'); assert.equal(std.name, '◇ 2 Standard clients'); assert.equal(std.detail, 'Production · Acme, Zeta'); assert.equal(std.trainId, 'prod')
  const od = day.get('2027-03-12')!.find(e => e.kind === 'client')!
  assert.equal(od.label, 'On-Demand'); assert.equal(od.name, '◇ 3 On-Demand clients'); assert.equal(od.detail, 'Production · Globex, Initech +1')
  assert.equal(day.get('2027-03-08')!.find(e => e.kind === 'client')!.detail, 'CERT · Globex')
  const wave = clientPayload(); wave.clientDates![2] = { ...wave.clientDates![2], date: '2027-03-05', wave: 'Standard' }
  assert.ok(entriesByDay(wave, '2027-03-01').get('2027-03-05')!.some(e => e.kind === 'client' && e.detail.endsWith('Standard wave')))
})

test('a release with only a client date in view still gets a row, with its type', () => {
  const p = clientPayload(); p.trains = p.trains.filter(t => t.id === 'prod'); p.clientDates = p.clientDates!.filter(c => c.segment === 'OnDemand' && c.trainId === 'prod'); p.gates = []
  const days = ['2027-03-08', '2027-03-09', '2027-03-10', '2027-03-11', '2027-03-12', '2027-03-13', '2027-03-14']
  const rows = releaseRows(p, entriesByDay(p, '2027-03-01'), days)
  assert.deepEqual(rows.map(r => [r.id, r.type]), [['prod', 'Production']])
})

test('filtering by segment narrows the releases and their client dates; combined keeps both segments; type and segment combine', () => {
  const ids = (d: CalendarPayload) => d.trains.map(t => t.id)
  const dates = (d: CalendarPayload) => d.clientDates!.map(c => `${c.trainId}:${c.segment}`)
  const p = clientPayload()
  assert.deepEqual(ids(applyFilters(p, NO_FILTER)), ['prod', 'cert', 'beta'])
  const std = applyFilters(p, { ...NO_FILTER, segment: 'Standard' })
  assert.deepEqual(ids(std), ['prod']); assert.deepEqual(dates(std), ['prod:Standard'])
  const od = applyFilters(p, { ...NO_FILTER, segment: 'OnDemand' })
  assert.deepEqual(ids(od), ['prod', 'cert']); assert.deepEqual(dates(od), ['cert:OnDemand', 'prod:OnDemand'])
  const both = applyFilters(p, { ...NO_FILTER, segment: 'Combined' })
  assert.deepEqual(ids(both), ['prod', 'cert']); assert.equal(both.clientDates!.length, 3)
  const mix = applyFilters(p, { ...NO_FILTER, segment: 'OnDemand', type: 'ty-cert' })
  assert.deepEqual(ids(mix), ['cert']); assert.deepEqual(dates(mix), ['cert:OnDemand'])
  assert.deepEqual(ids(applyFilters(p, { ...NO_FILTER, type: 'ty-beta' })), ['beta']); assert.deepEqual(dates(applyFilters(p, { ...NO_FILTER, type: 'ty-beta' })), [])
  assert.deepEqual(applyFilters(p, { ...NO_FILTER, segment: 'Standard' }).gates, [])   // CERT's gate follows its release out
  assert.equal(filtered({ ...NO_FILTER, segment: 'Combined' }), true)
})
