// REOS-176 (D56, D63): the change request screens' pure parts: words, tones, who may do what, form checks and request bodies. Status and risk are always words.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { navFor, placeholderOf } from '../src/shell/nav.ts'
import type { ChangeRow } from '../src/cab/cabApi.ts'
import {
  NONE, SEPARATION_RULES, canDecide, canDecideNow, canPlan, decisionTone, emptyForm, formOf, formProblem, isDirty, isRetroactive, isSeparationGuard, newBody, patchBody, riskSentence, riskTone, riskWord,
  selfApprovalReason, statusTone, stepsFor, summary,
} from '../src/cab/cabLogic.ts'

const row = (over: Partial<ChangeRow> = {}): ChangeRow => ({
  id: 'c1', key: 'CHG-0001', title: 'Patch', description: null, changeType: 'Normal', status: 'Draft', riskLevel: 'Low', riskFactors: null, requestedBy: { id: 'u1', name: 'Ada' }, createdBy: { id: 'u1', name: 'Ada' },
  release: null, goNoGoDecisionId: null, plannedStartAt: '2026-12-01T02:00:00Z', plannedEndAt: '2026-12-01T04:00:00Z', rollbackNote: 'Redeploy', submittedAt: null, decidedBy: null, decidedAt: null,
  implementedAt: null, closedAt: null, createdAt: '2026-11-26T10:00:00Z', lastChangedAt: null, version: 1, decisions: [], needsReview: false, risk: null, goNoGo: null, ...over,
})
const local = (iso: string) => iso.slice(0, 16)          // a stand-in for the display zone: the wall clock is the UTC clock
const utc = (l: string) => l + ':00Z'

test('the CAB entry is a real screen, not a placeholder, and still needs the module', () => {
  assert.equal(placeholderOf('cab'), undefined)
  assert.ok(!navFor(['RTE'], new Set()).flatMap(g => g.items).some(i => i.view === 'cab'))
  assert.ok(navFor(['Viewer'], new Set(['cab'])).flatMap(g => g.items).some(i => i.view === 'cab'))
})

test('every status and every risk level has a word and a tone; the chip never depends on colour', () => {
  for (const s of ['Draft', 'Submitted', 'Approved', 'Rejected', 'Implemented', 'Closed', 'Cancelled']) assert.ok(['success', 'info', 'warning', 'danger', 'neutral'].includes(statusTone(s)), s)
  assert.equal(statusTone('Rejected'), 'danger'); assert.equal(statusTone('Submitted'), 'warning'); assert.equal(statusTone('Approved'), 'success')
  assert.equal(riskTone('Low'), 'success'); assert.equal(riskTone('Medium'), 'warning'); assert.equal(riskTone('High'), 'danger'); assert.equal(riskTone('Critical'), 'danger')
  assert.equal(riskWord('High'), 'High risk')                      // the level is spelled out next to the dot
  assert.equal(decisionTone('Approved'), 'success'); assert.equal(decisionTone('Rejected'), 'danger'); assert.equal(decisionTone('Deferred'), 'neutral')
})

test('who may raise, edit, move and decide', () => {
  assert.ok(canPlan({ roles: ['RTE'] }) && canPlan({ roles: ['ReleaseManager'] }))
  assert.ok(!canPlan({ roles: ['Viewer'] }) && !canPlan({ roles: ['GovernanceOfficer'] }))
  assert.ok(canDecide({ roles: ['GovernanceOfficer'] }) && canDecide({ roles: ['RTE'] }) && !canDecide({ roles: ['Viewer'] }))
})

test('the moves open for each status; an Emergency change may be implemented before it is approved', () => {
  assert.deepEqual(stepsFor(row()).map(s => s.action), ['submit', 'cancel'])
  assert.deepEqual(stepsFor(row({ status: 'Submitted' })).map(s => s.action), ['cancel'])
  assert.deepEqual(stepsFor(row({ status: 'Submitted', changeType: 'Emergency' })).map(s => s.action), ['implement', 'cancel'])
  assert.deepEqual(stepsFor(row({ status: 'Approved' })).map(s => s.action), ['implement', 'cancel'])
  assert.deepEqual(stepsFor(row({ status: 'Implemented' })).map(s => s.action), ['close'])
  for (const s of ['Rejected', 'Closed', 'Cancelled']) assert.deepEqual(stepsFor(row({ status: s })), [])
  assert.ok(canDecideNow(row({ status: 'Submitted' })) && !canDecideNow(row()))
  assert.ok(isRetroactive(row({ status: 'Implemented', changeType: 'Emergency' })) && !isRetroactive(row({ status: 'Implemented' })))
  assert.ok(canDecideNow(row({ status: 'Implemented', changeType: 'Emergency' })) && !canDecideNow(row({ status: 'Implemented' })))
})

test('the requester is told in words why they cannot approve or reject, and the three rules are stated', () => {
  assert.match(selfApprovalReason(row(), { id: 'u1' })!, /you cannot approve or reject it/)
  assert.match(selfApprovalReason(row(), { id: 'u1' })!, /defer/)
  assert.equal(selfApprovalReason(row(), { id: 'u2' }), null)
  assert.equal(SEPARATION_RULES.length, 3)
  assert.ok(isSeparationGuard('ChangeSelfApproval') && isSeparationGuard('ChangeChairSoleApprover') && isSeparationGuard('ChangeEmergencyNeedsReview'))
  assert.ok(!isSeparationGuard('ChangeInvalid') && !isSeparationGuard(undefined))
})

test('the form says what is wrong in words, in the order a person fixes it', () => {
  const ok = { ...emptyForm('u1'), title: 'Patch' }
  assert.equal(formProblem(ok), null)
  assert.match(formProblem({ ...ok, title: '   ' })!, /title/)
  assert.match(formProblem({ ...ok, title: 'x'.repeat(201) })!, /200/)
  assert.match(formProblem({ ...ok, start: '2026-12-01T02:00' })!, /start and an end/)
  assert.match(formProblem({ ...ok, end: '2026-12-01T02:00' })!, /start and an end/)
  assert.match(formProblem({ ...ok, start: '2026-12-01T04:00', end: '2026-12-01T02:00' })!, /end after it starts/)
  assert.match(formProblem({ ...ok, start: '2026-12-01T04:00', end: '2026-12-01T04:00' })!, /end after it starts/)
  assert.match(formProblem({ ...ok, rollbackNote: 'x'.repeat(4001) })!, /4000/)
})

test('a new change sends the requester only when it is somebody else, converts times to UTC and leaves blanks null', () => {
  const f = { ...emptyForm('u1'), title: ' Patch ', start: '2026-12-01T02:00', end: '2026-12-01T04:00', rollbackNote: ' undo ', releaseId: 'r1' }
  const b = newBody(f, { id: 'u1' }, utc)
  assert.deepEqual(b, { title: 'Patch', changeType: 'Normal', description: null, requestedByUserId: null, releaseTrainId: 'r1', goNoGoDecisionId: null, plannedStartAt: '2026-12-01T02:00:00Z', plannedEndAt: '2026-12-01T04:00:00Z', rollbackNote: 'undo' })
  assert.equal(newBody({ ...f, requesterId: 'u2' }, { id: 'u1' }, utc).requestedByUserId, 'u2')
  assert.equal(newBody({ ...emptyForm('u1'), title: 't' }, { id: 'u1' }, utc).plannedStartAt, null)
})

test('an edit sends only what changed, and names an emptied field in clear', () => {
  const c = row({ release: { id: 'r1', title: 'R1' }, description: 'old', goNoGoDecisionId: 'g1' })
  const f = formOf(c, local)
  assert.equal(f.start, '2026-12-01T02:00')
  assert.deepEqual(patchBody(f, c, utc, local), {})
  assert.ok(!isDirty(f, c, local))
  assert.deepEqual(patchBody({ ...f, title: 'New' }, c, utc, local), { title: 'New' })
  assert.deepEqual(patchBody({ ...f, changeType: 'Emergency' }, c, utc, local), { changeType: 'Emergency' })
  const cleared = patchBody({ ...f, description: '', releaseId: NONE, goNoGoId: NONE, rollbackNote: '', start: '', end: '' }, c, utc, local)
  assert.deepEqual(cleared.clear!.sort(), ['description', 'goNoGoDecisionId', 'releaseTrainId', 'rollbackNote', 'window'])
  assert.equal(cleared.description, undefined)
  const moved = patchBody({ ...f, start: '2026-12-02T02:00', end: '2026-12-02T03:00' }, c, utc, local)
  assert.deepEqual([moved.plannedStartAt, moved.plannedEndAt], ['2026-12-02T02:00:00Z', '2026-12-02T03:00:00Z'])
  assert.ok(isDirty({ ...f, title: 'New' }, c, local))
})

test('the list footer counts changes and those waiting for the CAB', () => {
  assert.equal(summary([]), '0 changes, 0 waiting for the CAB')
  assert.equal(summary([row({ status: 'Submitted' })]), '1 change, 1 waiting for the CAB')
  assert.equal(summary([row({ status: 'Submitted' }), row(), row({ status: 'Approved' })]), '3 changes, 1 waiting for the CAB')
})

test('the risk sentence names every rule that applied and its points, so the list explains the level', () => {
  const factors = [
    { rule: 'Emergency', label: 'Emergency change', applied: true, points: 3, why: 'x' },
    { rule: 'FreezeWindow', label: 'Change inside or near a freeze', applied: false, points: 0, why: 'y' },
    { rule: 'NoRollbackNote', label: 'No rollback note', applied: true, points: 2, why: 'z' },
  ]
  assert.equal(riskSentence({ level: 'Critical', score: 5, levelRule: '', factors }), 'Critical risk: Emergency change (+3), No rollback note (+2). Score 5.')
  assert.equal(riskSentence({ level: 'Low', score: 0, levelRule: '', factors: factors.map(f => ({ ...f, applied: false, points: 0 })) }), 'Low risk: no rule applied (score 0).')
})
