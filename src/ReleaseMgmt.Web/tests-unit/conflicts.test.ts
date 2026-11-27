// REOS-180 (D56, D9): the conflict views' pure parts: words, counts, grouping, and the reason rule. A conflict warns and never blocks; approving over one needs a reason.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { Conflict } from '../src/cab/conflictsApi.ts'
import { DEFAULT_MIN_REASON, TYPE_ORDER, conflictCount, daysOf, groupByType, marker, needsReason, reasonHint } from '../src/cab/conflictLogic.ts'

const c = (type: Conflict['type'], rule: string): Conflict => ({ type, rule, ruleLabel: rule, summary: `${rule} found`, refId: null, refLabel: null, startsAt: null, endsAt: null })

test('the count is a word, never only a colour', () => {
  assert.equal(conflictCount(0), 'No conflicts'); assert.equal(conflictCount(1), '1 conflict'); assert.equal(conflictCount(4), '4 conflicts')
})

test('the marker is a shape: a diamond, with an exclamation mark when there are conflicts', () => {
  assert.equal(marker(0), '◆'); assert.equal(marker(2), '◆!')
})

test('conflicts are grouped in the fixed order of the four kinds and empty kinds are left out', () => {
  const g = groupByType([c('ReleaseWindow', 'ReleaseWindowOverlap'), c('Freeze', 'ChillOverlap'), c('Freeze', 'FreezeOverlap'), c('Blackout', 'BlackoutDay')])
  assert.deepEqual(g.map(x => x.type), ['Freeze', 'Blackout', 'ReleaseWindow'])
  assert.deepEqual(g[0].items.map(i => i.rule), ['ChillOverlap', 'FreezeOverlap'])
  assert.deepEqual(TYPE_ORDER, ['Freeze', 'Blackout', 'ChangeOverlap', 'ReleaseWindow'])
  assert.deepEqual(groupByType([]), [])
})

test('only an approval over a conflict needs a reason, and only a long enough one lets it through', () => {
  const some = [c('Freeze', 'FreezeOverlap')]
  assert.ok(needsReason('Approved', some, ''))
  assert.ok(needsReason('Approved', some, '   too short   '))
  assert.ok(!needsReason('Approved', some, 'Customer fix, the freeze lead agrees'))
  assert.ok(!needsReason('Approved', [], ''))            // a clear change needs none
  assert.ok(!needsReason('Rejected', some, ''))          // a rejection has its own rule; deferring and rejecting are never held for a conflict
  assert.ok(!needsReason('Deferred', some, ''))
  assert.equal(DEFAULT_MIN_REASON, 20)
  assert.ok(!needsReason('Approved', some, 'x'.repeat(5), 5))
})

test('the hint says it does not block and how long the reason is', () => {
  const t = reasonHint([c('Blackout', 'BlackoutDay'), c('Freeze', 'FreezeOverlap')])
  assert.match(t, /2 conflicts/); assert.match(t, /does not block/); assert.match(t, /20 characters/)
})

test('a window is placed on every UTC day it touches; ending at midnight does not touch the next day', () => {
  assert.deepEqual(daysOf('2033-03-05T02:00:00Z', '2033-03-05T04:00:00Z'), ['2033-03-05'])
  assert.deepEqual(daysOf('2033-03-05T22:00:00Z', '2033-03-06T00:00:00Z'), ['2033-03-05'])
  assert.deepEqual(daysOf('2033-03-05T22:00:00Z', '2033-03-07T01:00:00Z'), ['2033-03-05', '2033-03-06', '2033-03-07'])
})
