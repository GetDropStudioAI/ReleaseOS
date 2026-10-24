// REOS-117 (RT2): versions — how a chain reads, what "create the next release" asks for and why it is off, and the calendar payload carrying a release's chain.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { ChainWarning } from '../src/api.ts'
import { NO_FILTER, applyFilters, type CalendarPayload } from '../src/calendarModel.ts'
import type { ChainBrief, NextOption, VersionView } from '../src/versions/versionsApi.ts'
import { briefText, chainWords, defaultTitle, endOfChain, firstNext, isOpen, nextProblem, stepsOf, versionOption, warningsById } from '../src/versions/versionsLogic.ts'

const item = (id: string, label: string, status = 'Planning', target = '2026-11-10') => ({ trainId: id, key: null, title: `Orion ${label}`, label, typeId: 't', typeName: label, betaNumber: null, status, target, position: 1 })
const warn = (trainId: string): ChainWarning => ({ trainId, label: 'Beta 2', target: '2026-11-10', afterTrainId: 'b1', afterLabel: 'Beta 1', afterTarget: '2026-11-12', earliestOn: '2026-11-13', shortByDays: 3, message: 'Beta 2 is planned for 2026-11-10, but Beta 1 is due 2026-11-12' })
const version = (over: Partial<VersionView> = {}): VersionView =>
  ({ id: 'v1', name: 'Orion 5.2', archived: false, version: 1, createdAt: '2026-10-24T00:00:00Z', chain: [item('b1', 'Beta 1', 'Complete', '2026-11-12'), item('b2', 'Beta 2')], warnings: [warn('b2')], ...over })
const nextB2: NextOption = { typeId: 'tb', typeName: 'Beta', betaNumber: 2, label: 'Beta 2' }
const nextCert: NextOption = { typeId: 'tc', typeName: 'CERT', betaNumber: null, label: 'CERT' }

test('a chain reads as words: label, status and date of each step, in order', () => {
  assert.equal(chainWords(stepsOf(version())), 'Beta 1 (complete, 2026-11-12), then Beta 2 (planning, 2026-11-10)')
  assert.deepEqual(stepsOf(version()).map(s => s.label), ['Beta 1', 'Beta 2'])
})

test('the compact line under a release says the version, its label and its place in the chain', () => {
  const brief: ChainBrief = { versionId: 'v1', versionName: 'Orion 5.2', label: 'Beta 2', position: 2, count: 4, steps: [], warning: null }
  assert.equal(briefText(brief), 'Orion 5.2 · Beta 2, 2 of 4')
})

test('a warning is found by the release it is against', () => {
  assert.deepEqual(Object.keys(warningsById(version())), ['b2'])
  assert.match(warningsById(version()).b2, /Beta 2 is planned for 2026-11-10/)
  assert.deepEqual(warningsById(version({ warnings: [] })), {})
})

test('complete and aborted releases are closed, the others open', () => {
  assert.deepEqual(['Planning', 'Gated', 'Executing', 'Complete', 'Aborted'].map(isOpen), [true, true, true, false, false])
})

test('the next release starts on the first place after this one and is titled with the version and the label', () => {
  assert.equal(firstNext([nextB2, nextCert]), nextB2)
  assert.equal(firstNext([]), undefined)
  assert.equal(defaultTitle('Orion 5.2', nextB2), 'Orion 5.2 Beta 2')
  assert.equal(defaultTitle('Orion 5.2', nextCert), 'Orion 5.2 CERT')
  assert.equal(defaultTitle('Orion 5.2', undefined), '')
})

test('Create next release says why it is off, and is on when a place, a title and a date are given', () => {
  assert.equal(nextProblem(undefined, 'x', '2026-11-20'), 'Choose which release comes next.')
  assert.equal(nextProblem(nextB2, '   ', '2026-11-20'), 'Enter a title.')
  assert.equal(nextProblem(nextB2, 'Orion 5.2 Beta 2', ''), 'Choose a target date.')
  assert.equal(nextProblem(nextB2, 'Orion 5.2 Beta 2', '20/11/2026'), 'Choose a target date.')
  assert.equal(nextProblem(nextB2, 'Orion 5.2 Beta 2', '2026-11-20'), null)
})

test('the reason a chain cannot go on is in words: not in a version, archived, or no place left', () => {
  assert.match(endOfChain(null, [])!, /not in a version/)
  assert.match(endOfChain(version({ archived: true }), [nextCert])!, /archived/)
  assert.match(endOfChain(version(), [])!, /nowhere left to go/)
  assert.equal(endOfChain(version(), [nextCert]), null)
})

test('the version picker shows how many releases each version has', () => {
  assert.deepEqual(versionOption(version()), { value: 'v1', label: 'Orion 5.2 (2 releases)' })
  assert.equal(versionOption(version({ chain: [item('b1', 'Beta 1')] })).label, 'Orion 5.2 (1 release)')
})

test('a release in a version keeps its chain through the calendar filters (the filter limits the releases, not the chain)', () => {
  const brief: ChainBrief = { versionId: 'v1', versionName: 'Orion 5.2', label: 'Beta 1', position: 1, count: 2, steps: [{ trainId: 'a', label: 'Beta 1', status: 'Planning', target: '2026-11-10' }, { trainId: 'b', label: 'CERT', status: 'Planning', target: '2026-11-30' }], warning: null }
  const d: CalendarPayload = {
    from: '2026-11-01', to: '2026-11-30', gates: [], freezeWindows: [],
    trains: [
      { id: 'a', title: 'Orion Beta 1', status: 'Planning', riskTier: 'Low', targetDate: '2026-11-10', window: null, type: { id: 'tb', name: 'Beta' }, betaNumber: 1, chain: brief },
      { id: 'b', title: 'Orion CERT', status: 'Planning', riskTier: 'Low', targetDate: '2026-11-30', window: null, type: { id: 'tc', name: 'CERT' }, chain: { ...brief, label: 'CERT', position: 2 } },
    ],
  }
  const shown = applyFilters(d, { ...NO_FILTER, type: 'tb' })
  assert.deepEqual(shown.trains.map(t => t.id), ['a'])
  assert.equal(shown.trains[0].chain?.count, 2)
  assert.equal(shown.trains[0].chain?.steps.length, 2)
})
