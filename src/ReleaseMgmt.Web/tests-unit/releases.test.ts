// REOS-125 (D50): the Releases table's filters, their address-bar form, the order and the gate rollup.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { StreamRow } from '../src/api.ts'
import { ALL, NONE, NO_FILTERS, filterReleases, filtersFromSearch, filtersToSearch, gateRollup, isFiltered, optionsOf, sortReleases, teamLabel } from '../src/releases/releasesLogic.ts'

const ops = { id: 'tm1', handle: 'ops', name: 'Operations' }
const cert = { id: 'ty1', name: 'CERT' }, prod = { id: 'ty2', name: 'Production' }
const stg = { id: 'e1', name: 'Staging', kind: 'Staging', region: null }, prd = { id: 'e2', name: 'Production', kind: 'Production', region: 'eu' }
const row = (id: string, over: Partial<StreamRow> = {}): StreamRow => ({
  id, title: `R27.${id} Card portal`, status: 'Planning', riskTier: 'Low', targetReleaseDate: '2026-11-10', daysToTarget: 12, blockers: 0, gates: [], closeCode: null, endedOn: null, version: 1,
  key: `RLS-${id}`, owner: null, ownerTeam: null, type: null, environments: [], ...over })
const rows = [
  row('1', { status: 'Executing', owner: { id: 'u1', name: 'Priya Shah' }, ownerTeam: ops, type: cert, environments: [stg, prd], targetReleaseDate: '2026-11-01' }),
  row('2', { status: 'Complete', closeCode: 'Successful', type: prod, environments: [prd], targetReleaseDate: '2026-10-01' }),
  row('3', { status: 'Planning' }),
  row('10', { status: 'Planning', targetReleaseDate: '2026-11-10' }),
  row('4', { status: 'Gated', ownerTeam: ops, targetReleaseDate: '2026-11-05' }),
]
const ids = (rs: StreamRow[]) => rs.map(r => r.id)

test('no filter shows every release; each filter narrows on its own field', () => {
  assert.deepEqual(ids(filterReleases(rows, NO_FILTERS)), ['1', '2', '3', '10', '4'])
  assert.deepEqual(ids(filterReleases(rows, { ...NO_FILTERS, status: 'Planning' })), ['3', '10'])
  assert.deepEqual(ids(filterReleases(rows, { ...NO_FILTERS, team: 'tm1' })), ['1', '4'])
  assert.deepEqual(ids(filterReleases(rows, { ...NO_FILTERS, environment: 'e2' })), ['1', '2'])
  assert.deepEqual(ids(filterReleases(rows, { ...NO_FILTERS, type: 'ty2' })), ['2'])
})

test('filters combine, and "not set" finds the releases with nothing recorded', () => {
  assert.deepEqual(ids(filterReleases(rows, { ...NO_FILTERS, team: 'tm1', status: 'Gated' })), ['4'])
  assert.deepEqual(ids(filterReleases(rows, { ...NO_FILTERS, team: NONE })), ['2', '3', '10'])
  assert.deepEqual(ids(filterReleases(rows, { ...NO_FILTERS, environment: NONE })), ['3', '10', '4'])
  assert.deepEqual(ids(filterReleases(rows, { ...NO_FILTERS, type: NONE })), ['3', '10', '4'])
  assert.deepEqual(ids(filterReleases(rows, { ...NO_FILTERS, status: 'Aborted' })), [])
})

test('search matches key, title, owner, team, type and environment, ignoring case', () => {
  const q = (query: string) => ids(filterReleases(rows, { ...NO_FILTERS, query }))
  assert.deepEqual(q('rls-10'), ['10'])
  assert.deepEqual(q('PRIYA'), ['1'])
  assert.deepEqual(q('operations'), ['1', '4'])
  assert.deepEqual(q('cert'), ['1'])
  assert.deepEqual(q('  staging '), ['1'])
  assert.deepEqual(q('card portal').length, 5)
})

test('the order is open work first, then by target date, then by key as a number', () => {
  assert.deepEqual(ids(sortReleases(rows)), ['1', '4', '3', '10', '2'])
  assert.deepEqual(ids(sortReleases([row('9', { status: 'Mystery' }), row('8', { status: 'Aborted' }), row('7')])), ['7', '8', '9'])
})

test('the filter options list what the releases have, sorted, with Not set last only when some release has none', () => {
  const t = optionsOf(rows, r => (r.ownerTeam ? [{ id: r.ownerTeam.id, label: teamLabel(r)! }] : []), 'All teams', 'No team')
  assert.deepEqual(t, [{ value: ALL, label: 'All teams' }, { value: 'tm1', label: '@ops' }, { value: NONE, label: 'No team' }])
  const e = optionsOf(rows, r => r.environments.map(x => ({ id: x.id, label: x.name })), 'All environments', 'No environment')
  assert.deepEqual(e.map(o => o.label), ['All environments', 'Production', 'Staging', 'No environment'])
  assert.deepEqual(optionsOf([rows[0]], r => (r.type ? [{ id: r.type.id, label: r.type.name }] : []), 'All types', 'No type').map(o => o.label), ['All types', 'CERT'])
})

test('filters round-trip through the address bar; empty filters leave it clean', () => {
  assert.equal(filtersToSearch(NO_FILTERS), '')
  const f = { query: 'card', status: 'Gated', team: 'tm1', environment: 'e2', type: NONE }
  assert.deepEqual(filtersFromSearch(filtersToSearch(f)), f)
  assert.deepEqual(filtersFromSearch(''), NO_FILTERS)
  assert.equal(isFiltered(NO_FILTERS), false)
  assert.equal(isFiltered({ ...NO_FILTERS, query: '  ' }), false)
  assert.equal(isFiltered({ ...NO_FILTERS, type: 'ty1' }), true)
})

test('the gate rollup counts Certified and Waived as passed and names failures', () => {
  assert.deepEqual(gateRollup(['Certified', 'Waived', 'InProgress', 'Failed', 'Pending']), { passed: 2, total: 5, failed: 1, words: '2 of 5 passed' })
  assert.equal(gateRollup([]).words, 'No gates')
})
