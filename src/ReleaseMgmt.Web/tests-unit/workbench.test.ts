// REOS-179 (D56, D63): the CAB workbench's pure parts: who may run the meeting, what is still to decide, the flags next to an item, the decision checks, completion and the per-item timer.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { ChangeRow, DecisionRow } from '../src/cab/cabApi.ts'
import type { AgendaItem, Attendee, Meeting } from '../src/cab/workbenchApi.ts'
import {
  canDecideHere, canRun, clock, completeWarning, connectedCount, decidedRows, decisionBlock, defaultSelection, flagsFor, meetingStatusWord, newTimer, outcomeOf, pauseTimer, pendingRows,
  positionAfterSkip, positionForTakeNext, recordOf, resetTimer, roleWord, rowsOf, spentOn, startTimer, summaryLine,
} from '../src/cab/workbenchLogic.ts'

const dec = (over: Partial<DecisionRow> = {}): DecisionRow => ({ id: 'd1', decision: 'Approved', retroactive: false, meetingId: 'm1', rationale: 'ok', decidedBy: { id: 'u2', name: 'Bo' }, decidedAt: '2026-12-01T02:10:00Z', ...over })
const change = (over: Partial<ChangeRow> = {}): ChangeRow => ({
  id: 'c1', key: 'CHG-0001', title: 'Patch', description: null, changeType: 'Normal', status: 'Submitted', riskLevel: 'Low', riskFactors: null, requestedBy: { id: 'u1', name: 'Ada' }, createdBy: { id: 'u1', name: 'Ada' },
  release: null, goNoGoDecisionId: null, plannedStartAt: '2026-12-01T02:00:00Z', plannedEndAt: '2026-12-01T04:00:00Z', rollbackNote: 'Redeploy', submittedAt: null, decidedBy: null, decidedAt: null,
  implementedAt: null, closedAt: null, createdAt: '2026-11-26T10:00:00Z', lastChangedAt: null, version: 2, decisions: [], needsReview: false, risk: null, goNoGo: null, ...over,
})
const item = (n: number, over: Partial<AgendaItem> = {}): AgendaItem => ({ id: `i${n}`, changeRequestId: `c${n}`, changeKey: `CHG-000${n}`, changeTitle: `Change ${n}`, changeStatus: 'Submitted', sequenceOrder: n, notes: null, version: 1, ...over })
const meeting = (agenda: AgendaItem[], over: Partial<Meeting> = {}): Meeting => ({
  id: 'm1', title: 'Weekly CAB', startsAt: '2026-12-01T02:00:00Z', endsAt: '2026-12-01T04:00:00Z', chair: { id: 'u9', name: 'Chair' }, status: 'InProgress', location: null, minutes: null,
  createdAt: '2026-11-26T10:00:00Z', lastChangedAt: null, version: 3, agenda, attendees: [], decisions: [], completedAt: null, ...over,
})
const byId = (...cs: ChangeRow[]) => new Map(cs.map(c => [c.id, c]))
const factor = (points: number): NonNullable<ChangeRow['risk']> => ({ level: 'High', score: points, levelRule: '', factors: [{ rule: 'FreezeWindow', label: 'Change inside or near a freeze', applied: true, points, why: 'Overlaps Year-end freeze.' }] })

test('status is a word, and only a Plan role runs an open meeting; only deciders decide an in-progress one', () => {
  assert.equal(meetingStatusWord('InProgress'), 'In progress')
  assert.equal(meetingStatusWord('Completed'), 'Complete')
  assert.equal(canRun({ roles: ['RTE'] }, { status: 'Planned' }), true)
  assert.equal(canRun({ roles: ['ReleaseManager'] }, { status: 'InProgress' }), true)
  assert.equal(canRun({ roles: ['GovernanceOfficer'] }, { status: 'InProgress' }), false)
  assert.equal(canRun({ roles: ['RTE'] }, { status: 'Completed' }), false)
  assert.equal(canDecideHere({ roles: ['GovernanceOfficer'] }, { status: 'InProgress' }), true)
  assert.equal(canDecideHere({ roles: ['GovernanceOfficer'] }, { status: 'Planned' }), false)
  assert.equal(canDecideHere({ roles: ['Viewer'] }, { status: 'InProgress' }), false)
  assert.equal(canDecideHere({ roles: ['RTE'] }, { status: 'Completed' }), false)
})

test('an item is decided by this meeting, by an outcome elsewhere, or still pending; Deferred stays on the table', () => {
  assert.equal(outcomeOf(change(), 'm1'), null)
  assert.equal(outcomeOf(change({ decisions: [dec({ decision: 'Deferred' })] }), 'm1'), 'Deferred')
  assert.equal(outcomeOf(change({ status: 'Approved', decisions: [dec({ meetingId: null })] }), 'm1'), 'Approved')
  assert.equal(outcomeOf(change({ decisions: [dec({ meetingId: 'other', decision: 'Rejected' })] }), 'm1'), null)
  assert.equal(outcomeOf(change({ decisions: [dec({ decision: 'Deferred', decidedAt: '2026-12-01T02:05:00Z' }), dec({ id: 'd2', decision: 'Approved' })] }), 'm1'), 'Approved')   // the latest here counts
  assert.equal(outcomeOf(change({ decisions: [dec({ retroactive: true })] }), 'm1'), null)
  const m = meeting([item(2), item(1), item(3)])
  const rows = rowsOf(m, byId(change({ id: 'c1' }), change({ id: 'c2', key: 'CHG-0002', decisions: [dec()], status: 'Approved' }), change({ id: 'c3', key: 'CHG-0003', decisions: [dec({ decision: 'Deferred' })] })))
  assert.deepEqual(rows.map(r => r.item.changeKey), ['CHG-0001', 'CHG-0002', 'CHG-0003'])   // by position, not by arrival
  assert.deepEqual(pendingRows(rows).map(r => r.item.changeKey), ['CHG-0001', 'CHG-0003'])
  assert.deepEqual(decidedRows(rows).map(r => r.item.changeKey), ['CHG-0002'])
  assert.equal(defaultSelection(rows), 'i1')
  assert.equal(summaryLine(rows), '3 changes on the agenda, 2 still to decide')
  assert.equal(defaultSelection([]), null)
})

test('skip puts an item last, take next puts it first among those still to decide', () => {
  const m = meeting([item(1), item(2), item(3), item(4)])
  const rows = rowsOf(m, byId(change({ id: 'c1' }), change({ id: 'c2', decisions: [dec()], status: 'Approved' }), change({ id: 'c3' }), change({ id: 'c4' })))
  assert.equal(positionAfterSkip(rows), 4)
  assert.equal(positionForTakeNext(rows, 'i4'), 1)     // first still to decide is CHG-1 at position 1
  const behind = rowsOf(meeting([item(1), item(2), item(3)]), byId(change({ id: 'c1', decisions: [dec()], status: 'Approved' }), change({ id: 'c2' }), change({ id: 'c3' })))
  assert.equal(positionForTakeNext(behind, 'i3'), 2)   // the decided one stays above
  assert.equal(positionForTakeNext(behind, 'i2'), 2)   // already next: no move
})

test('flags: the freeze rule in words (blackout, chill, near) and overlapping windows on the same agenda; none says so', () => {
  assert.deepEqual(flagsFor(change(), []), [])
  assert.deepEqual(flagsFor(undefined, []), [])
  const inFreeze = flagsFor(change({ risk: factor(3) }), [])
  assert.equal(inFreeze[0].kind, 'Blackout')
  assert.match(inFreeze[0].label, /freeze \(blackout\)/)
  assert.equal(inFreeze[0].detail, 'Overlaps Year-end freeze.')
  assert.equal(flagsFor(change({ risk: factor(2) }), [])[0].label, 'Inside a chill window')
  assert.equal(flagsFor(change({ risk: factor(1) }), [])[0].label, 'Near a freeze or chill window')
  const off = change({ risk: { ...factor(3), factors: [{ ...factor(3).factors[0], applied: false }] } })
  assert.deepEqual(flagsFor(off, []), [])
  const a = change({ id: 'c1' }), b = change({ id: 'c2', key: 'CHG-0002', title: 'Other', plannedStartAt: '2026-12-01T03:00:00Z', plannedEndAt: '2026-12-01T05:00:00Z' })
  const touching = change({ id: 'c3', key: 'CHG-0003', plannedStartAt: '2026-12-01T04:00:00Z', plannedEndAt: '2026-12-01T05:00:00Z' })
  const none = change({ id: 'c4', key: 'CHG-0004', plannedStartAt: null, plannedEndAt: null })
  const f = flagsFor(a, [a, b, touching, none])
  assert.equal(f.length, 1)
  assert.equal(f[0].kind, 'Conflict')
  assert.equal(f[0].label, 'Overlaps CHG-0002')   // a window that only touches, and one with no window, are not conflicts
  assert.equal(f.every(x => x.tone !== 'neutral'), true)
})

test('approve and reject need a comment and cannot be the requester; deferring needs neither', () => {
  const c = change(), me = { id: 'u2' }
  assert.equal(decisionBlock('Approved', '', c, me), 'An approval needs a comment.')
  assert.equal(decisionBlock('Rejected', '  ', c, me), 'A rejection needs a comment.')
  assert.equal(decisionBlock('Approved', 'Rollback tested', c, me), null)
  assert.equal(decisionBlock('Deferred', '', c, me), null)
  assert.match(decisionBlock('Approved', 'x', c, { id: 'u1' })!, /you cannot approve or reject/)   // the requester: refused in words before any request
  assert.match(decisionBlock('Rejected', '', c, { id: 'u1' })!, /you cannot approve or reject/)
  assert.equal(decisionBlock('Deferred', '', c, { id: 'u1' }), null)
})

test('the record lists this meeting\'s decisions oldest first, with the change each was about', () => {
  const m = meeting([item(1), item(2)])
  const rows = rowsOf(m, byId(
    change({ id: 'c1', decisions: [dec({ id: 'd2', decidedAt: '2026-12-01T02:20:00Z' }), dec({ id: 'd0', meetingId: 'other' })] }),
    change({ id: 'c2', key: 'CHG-0002', decisions: [dec({ id: 'd1', decision: 'Rejected', decidedAt: '2026-12-01T02:10:00Z' })] })))
  assert.deepEqual(recordOf(rows, 'm1').map(r => `${r.change.key}:${r.decision.id}`), ['CHG-0002:d1', 'CHG-0001:d2'])
})

test('completing says how many items have no decision', () => {
  const m = meeting([item(1), item(2)])
  assert.equal(completeWarning(rowsOf(m, byId(change({ id: 'c1' }), change({ id: 'c2' })))), '2 changes have no decision yet and will stay as they are (Submitted).')
  assert.equal(completeWarning(rowsOf(m, byId(change({ id: 'c1', decisions: [dec()] }), change({ id: 'c2' })))), '1 change has no decision yet and will stay as they are (Submitted).')
  assert.equal(completeWarning(rowsOf(m, byId(change({ id: 'c1', decisions: [dec()] }), change({ id: 'c2', decisions: [dec({ id: 'd2' })] })))), 'Every change on the agenda has a decision.')
})

test('attendees: the chair is named, and connected people are counted', () => {
  const a = (id: string, isChair = false): Attendee => ({ person: { id, name: id }, attendeeRole: 'Member', attendance: 'Invited', isChair, version: 1 })
  assert.equal(roleWord(a('u1', true)), 'Chair')
  assert.equal(roleWord(a('u2')), 'Member')
  assert.equal(connectedCount([a('u1'), a('u2'), a('u3')], new Set(['u2', 'u9'])), 1)
})

test('the timer runs on one item at a time, pauses, resumes and resets; never negative', () => {
  assert.equal(clock(0), '00:00'); assert.equal(clock(65_000), '01:05'); assert.equal(clock(3_723_000), '1:02:03'); assert.equal(clock(-5), '00:00')
  let t = newTimer()
  assert.equal(spentOn(t, 'a', 1000), 0)
  t = startTimer(t, 'a', 1000)
  assert.equal(spentOn(t, 'a', 6000), 5000)
  t = startTimer(t, 'b', 6000)                      // starting b pauses a
  assert.equal(t.running, 'b')
  assert.equal(spentOn(t, 'a', 99_000), 5000)
  assert.equal(spentOn(t, 'b', 9000), 3000)
  t = pauseTimer(t, 9000)
  assert.equal(t.running, null)
  t = startTimer(t, 'a', 20_000)                    // resume a: the 5 s are kept
  assert.equal(spentOn(t, 'a', 22_000), 7000)
  t = resetTimer(t, 'a')
  assert.equal(t.running, null)
  assert.equal(spentOn(t, 'a', 30_000), 0)
  assert.equal(spentOn(t, 'b', 30_000), 3000)
  assert.equal(spentOn(startTimer(newTimer(), 'a', 5000), 'a', 1000), 0)   // a clock that went backwards
})
