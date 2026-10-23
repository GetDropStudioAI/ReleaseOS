// REOS-99: the Approvals screen's pure parts: references, override ids, the due-time countdown and its overdue flip, the metric numbers and the note rules.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { EXCEPTION_KINDS, MIN_NOTE, countdown, dueWithinHour, itemKey, noteProblem, overdueCount, parseOverrideId, shortRef } from '../src/shell/approvalsLogic.ts'

const NOW = Date.parse('2026-10-23T14:32:00Z')
const timed = (iso: string) => ({ dueOn: null, dueAt: iso, overdue: Date.parse(iso) <= NOW })

test('an item is keyed by kind and id, and its reference is a short stable code from the id', () => {
  assert.equal(itemKey({ kind: 'CertifyGate', id: 'g1' }), 'CertifyGate:g1')
  assert.equal(shortRef({ kind: 'CertifyGate', id: '0198a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b' }), 'CRT-3F4A5B')
  assert.equal(shortRef({ kind: 'GrantOverride', id: 'w9z8y7:t1:u1' }), 'OVR-W9Z8Y7')   // an override's id is compound: the window's part names it
  assert.deepEqual(EXCEPTION_KINDS, ['ApproveWaiver', 'GrantOverride'])
})

test('an override id splits into window, train and requester, and anything else is refused', () => {
  assert.deepEqual(parseOverrideId('w:t:u'), { windowId: 'w', trainId: 't', requesterId: 'u' })
  assert.equal(parseOverrideId('w:t'), null)
  assert.equal(parseOverrideId('w:t:u:x'), null)
})

test('a timed decision counts down, and flips to overdue once its time has passed', () => {
  assert.deepEqual(countdown(timed('2026-10-23T15:00:00Z'), NOW), { text: 'Due in 28m', overdue: false, soon: true })
  assert.equal(countdown(timed('2026-10-23T17:07:00Z'), NOW).text, 'Due in 2h 35m')
  assert.equal(countdown(timed('2026-10-23T18:32:00Z'), NOW).text, 'Due in 4h')
  assert.equal(countdown(timed('2026-10-26T14:32:00Z'), NOW).text, 'Due in 3d')
  assert.equal(countdown(timed('2026-10-23T17:07:00Z'), NOW).soon, false)
  const late = countdown(timed('2026-10-23T14:20:00Z'), NOW)
  assert.deepEqual(late, { text: 'Overdue by 12m', overdue: true, soon: false })
  assert.equal(countdown(timed('2026-10-23T14:32:00Z'), NOW).text, 'Due now')
  // the same item a minute later: the countdown moved, the flip needs no reload
  assert.equal(countdown(timed('2026-10-23T14:33:00Z'), NOW).text, 'Due in 1m')
  assert.equal(countdown(timed('2026-10-23T14:33:00Z'), NOW + 120000).text, 'Overdue by 1m')
})

test('a dated decision shows its day, and says overdue in words once the server flags it', () => {
  assert.deepEqual(countdown({ dueOn: '2026-10-28', dueAt: null, overdue: false }, NOW), { text: 'Due 28 Oct', overdue: false, soon: false })
  assert.equal(countdown({ dueOn: '2027-01-05', dueAt: null, overdue: false }, NOW).text, 'Due 5 Jan 2027')
  assert.deepEqual(countdown({ dueOn: '2026-10-20', dueAt: null, overdue: true }, NOW), { text: 'Overdue, was due 20 Oct', overdue: true, soon: false })
})

test('the metric numbers: due within an hour counts timed items still ahead, overdue counts both kinds', () => {
  const items = [timed('2026-10-23T15:00:00Z'), timed('2026-10-23T15:32:00Z'), timed('2026-10-23T15:33:00Z'), timed('2026-10-23T14:00:00Z'), { dueOn: '2026-10-20', dueAt: null, overdue: true }, { dueOn: '2026-10-29', dueAt: null, overdue: false }]
  assert.equal(dueWithinHour(items, NOW), 2)
  assert.equal(overdueCount(items, NOW), 2)
})

test('the decision note: required for No-Go and for an override (20 characters), never for a plain Go', () => {
  assert.match(noteProblem('nogo', '   ')!, /required to record No-Go/)
  assert.equal(noteProblem('nogo', 'Rollback not rehearsed'), null)
  assert.equal(noteProblem('go', ''), null)
  assert.match(noteProblem('override', 'too short')!, new RegExp(`at least ${MIN_NOTE} characters \\(9 so far\\)`))
  assert.equal(noteProblem('override', 'The payment freeze blocks a critical fix'), null)
})
