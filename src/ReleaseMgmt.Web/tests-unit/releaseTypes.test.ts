// REOS-116 (RT1): release types — how a type reads with its Beta number, what a new release asks for, and the type filters of the lists, the calendar and analytics.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { StreamRow } from '../src/api.ts'
import { BETA_NUMBERS, activeTypes, betaLockReason, sortTypes, startsFrom, typeLabel, typeProblem, type ReleaseTypeRow } from '../src/mgmt/releaseTypesLogic.ts'
import { ALL, NO_FILTERS, filterReleases } from '../src/releases/releasesLogic.ts'
import { NO_FILTER, applyFilters, filtered, type CalendarPayload } from '../src/calendarModel.ts'
import { analyticsQuery, typeParam, workbookUrl } from '../src/analyticsExport.ts'

const ty = (id: string, name: string, over: Partial<ReleaseTypeRow> = {}): ReleaseTypeRow =>
  ({ id, name, sortOrder: 10, defaultTemplate: null, archived: false, releases: 0, version: 1, isBeta: false, ...over })
const beta = ty('b', 'Beta', { sortOrder: 10, isBeta: true, releases: 2 })
const cert = ty('c', 'CERT', { sortOrder: 20, defaultTemplate: { id: 'tp1', name: 'CERT standard' } })
const pen = ty('p', 'PenTest Cert', { sortOrder: 30 })
const prod = ty('pr', 'Production', { sortOrder: 40 })
const old = ty('o', 'Legacy', { sortOrder: 5, archived: true })

test('a type reads with its Beta number: Beta 2, CERT, nothing when no type is recorded', () => {
  assert.equal(typeLabel({ name: 'Beta' }, 2), 'Beta 2')
  assert.equal(typeLabel({ name: 'CERT' }, null), 'CERT')
  assert.equal(typeLabel({ name: 'CERT' }), 'CERT')
  assert.equal(typeLabel(null, 3), '')
})

test('a new release can take active types only, in the workspace order', () => {
  assert.deepEqual(activeTypes([prod, old, cert, beta, pen]).map(t => t.name), ['Beta', 'CERT', 'PenTest Cert', 'Production'])
  assert.deepEqual(sortTypes([old, prod, beta]).map(t => t.name), ['Beta', 'Production', 'Legacy'])
})

test('Create asks for a type once the workspace has types, and for the number of a Beta release', () => {
  const all = [beta, cert, pen, prod, old]
  assert.equal(typeProblem(all, '', ''), 'Choose a release type.')
  assert.equal(typeProblem(all, 'o', ''), 'Choose a release type.', 'an archived type cannot be chosen')
  assert.equal(typeProblem(all, 'gone', ''), 'Choose a release type.')
  assert.equal(typeProblem(all, 'c', ''), null)
  assert.equal(typeProblem(all, 'b', ''), 'Choose the number of this Beta release, 1 to 3.')
  assert.equal(typeProblem(all, 'b', '4'), 'Choose the number of this Beta release, 1 to 3.')
  for (const n of BETA_NUMBERS) assert.equal(typeProblem(all, 'b', String(n)), null)
  assert.equal(typeProblem([], '', ''), null, 'a workspace with no types asks for none')
  assert.equal(typeProblem([old], '', ''), null, 'nor does one whose types are all archived')
})

test('a release of a type starts from its default template, when it has one', () => {
  assert.equal(startsFrom(cert), 'CERT standard')
  assert.equal(startsFrom(prod), null)
  assert.equal(startsFrom(undefined), null)
})

test('a type that releases use cannot become or stop being a Beta type', () => {
  assert.match(betaLockReason(beta)!, /2 releases use this type/)
  assert.match(betaLockReason(ty('x', 'X', { releases: 1 }))!, /1 release uses this type/)
  assert.equal(betaLockReason(prod), null)
})

const row = (id: string, type: { id: string; name: string } | null, betaNumber: number | null = null): StreamRow => ({
  id, title: `R${id}`, status: 'Planning', riskTier: 'Low', targetReleaseDate: '2026-11-10', daysToTarget: 12, blockers: 0, gates: [], closeCode: null, endedOn: null, version: 1,
  key: `RLS-${id}`, owner: null, ownerTeam: null, type, environments: [], betaNumber })

test('the releases list filters by type and finds "Beta 2" by search', () => {
  const rows = [row('1', { id: 'b', name: 'Beta' }, 1), row('2', { id: 'b', name: 'Beta' }, 2), row('3', { id: 'pr', name: 'Production' }), row('4', null)]
  const ids = (f: Partial<typeof NO_FILTERS>) => filterReleases(rows, { ...NO_FILTERS, ...f }).map(r => r.id)
  assert.deepEqual(ids({}), ['1', '2', '3', '4'])
  assert.deepEqual(ids({ type: 'b' }), ['1', '2'])
  assert.deepEqual(ids({ type: 'pr' }), ['3'])
  assert.deepEqual(ids({ type: 'c' }), [])
  assert.deepEqual(ids({ query: 'beta 2' }), ['2'])
  assert.equal(ALL, '')
})

const cal = (): CalendarPayload => ({
  from: '2026-10-01', to: '2026-10-31', freezeWindows: [{ id: 'f', name: 'Year end', kind: 'Freeze', startsAt: '2026-10-10T00:00:00Z', endsAt: '2026-10-12T00:00:00Z', scope: 'All products' }],
  trains: [
    { id: 't1', title: 'A', status: 'Planning', riskTier: 'Low', targetDate: '2026-10-20', window: null, type: { id: 'b', name: 'Beta' }, betaNumber: 1 },
    { id: 't2', title: 'B', status: 'Planning', riskTier: 'Low', targetDate: '2026-10-21', window: null, type: { id: 'pr', name: 'Production' } },
    { id: 't3', title: 'C', status: 'Planning', riskTier: 'Low', targetDate: '2026-10-22', window: null },
  ],
  gates: [{ id: 'g1', name: 'CAB', class: 'Compliance', status: 'Pending', dueOn: '2026-10-15', trainId: 't1', trainTitle: 'A', trainStatus: 'Planning' },
    { id: 'g2', name: 'CAB', class: 'Compliance', status: 'Pending', dueOn: '2026-10-16', trainId: 't2', trainTitle: 'B', trainStatus: 'Planning' }],
  milestones: [{ id: 'm1', name: 'UAT', dueOn: '2026-10-14', done: false, trainId: 't2', trainTitle: 'B', trainStatus: 'Planning' }],
})

test('the calendar filters releases, their gates and milestones by type; freezes stay', () => {
  assert.equal(filtered(NO_FILTER), false)
  assert.equal(filtered({ ...NO_FILTER, type: 'b' }), true)
  const beta = applyFilters(cal(), { ...NO_FILTER, type: 'b' })
  assert.deepEqual(beta.trains.map(t => t.id), ['t1'])
  assert.deepEqual(beta.gates.map(g => g.id), ['g1'])
  assert.deepEqual(beta.milestones, [])
  assert.equal(beta.freezeWindows.length, 1)
  const prod = applyFilters(cal(), { ...NO_FILTER, type: 'pr' })
  assert.deepEqual(prod.trains.map(t => t.id), ['t2'])
  assert.deepEqual(prod.milestones!.map(m => m.id), ['m1'])
  assert.deepEqual(applyFilters(cal(), { ...NO_FILTER, type: 'none-such' }).trains, [], 'a type nobody has leaves no release')
  assert.deepEqual(applyFilters(cal(), { ...NO_FILTER, type: 'b', status: 'Complete' }).trains, [], 'type and status both apply')
})

test('analytics asks the server for one type through releaseTypeId, and leaves it out for all types', () => {
  assert.equal(typeParam(''), '')
  assert.equal(typeParam('a b'), '&releaseTypeId=a%20b')
  assert.equal(analyticsQuery('', '', ''), '')
  assert.equal(analyticsQuery('2026-01-01', '2026-03-31', ''), '?from=2026-01-01&to=2026-03-31')
  assert.equal(analyticsQuery('2026-01-01', '2026-03-31', 'ty1'), '?from=2026-01-01&to=2026-03-31&releaseTypeId=ty1')
  assert.equal(analyticsQuery('', '', 'ty1'), '?releaseTypeId=ty1')
  assert.equal(workbookUrl('2026-01-01', '2026-03-31', '2026-04-01T00:00:00Z'), '/api/v1/analytics/xlsx?from=2026-01-01&to=2026-03-31&now=2026-04-01T00%3A00%3A00Z')
  assert.ok(workbookUrl('2026-01-01', '2026-03-31', '2026-04-01T00:00:00Z', 'ty1').endsWith('&releaseTypeId=ty1'))
})
