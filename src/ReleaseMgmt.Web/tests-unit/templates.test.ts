// REOS-122 (RT7): template governance on the Templates screen — tasks on gates, milestones in days from the target, versions and who may approve.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { Me, TemplateDetail, TemplateGate, TemplateRow } from '../src/api.ts'
import { addTask, approveBlock, blankInput, count, draftProblem, milestoneOffset, milestoneSentence, moveTask, removeTask, taskLines, toInput, updateTask, versionLabel } from '../src/templateLogic.ts'

const row = (over: Partial<TemplateRow> = {}): TemplateRow => ({
  id: 't2', name: 'Quarterly', status: 'Draft', defaultRiskTier: 'High', approvedByUserId: null, approvedByName: null, approvedAt: null, reviewDueOn: null, reviewOverdue: false,
  gateCount: 2, stepCount: 0, scheduleCount: 0, version: 3, versionNo: 2, supersedesTemplateId: 't1', supersededByTemplateId: null,
  createdByUserId: 'u-author', createdByName: 'Ann Author', lastEditedByUserId: 'u-author', lastEditedByName: 'Ann Author', taskCount: 2, milestoneCount: 1, ...over,
})
const gates = (): TemplateGate[] => [
  { gateName: 'Code Freeze', gateClass: 'Standard', offsetDays: 5, requiredBeforeStatus: 'Gated', ownerTeamId: null, tasks: [{ taskDescription: 'Tag repos', ownerTeamId: null }, { taskDescription: 'Freeze branch', ownerTeamId: 'tm1' }] },
  { gateName: 'CAB', gateClass: 'Compliance', offsetDays: 1, requiredBeforeStatus: 'Executing', ownerTeamId: null, tasks: [] },
]
const detail = (over: Partial<TemplateDetail> = {}, t: Partial<TemplateRow> = {}): TemplateDetail => ({
  template: row(t), gates: gates(), steps: [], schedule: [], milestones: [{ name: 'Code complete', offsetDays: -14, ownerTeamId: null }],
  versions: [], changes: [], complianceChanged: false, needsGovernanceApproval: false, ...over,
})
const me = (id: string, ...roles: string[]): Me => ({ id, email: `${id}@x.com`, name: id, roles })

test('a milestone reads as T−n before the target, T+n after it and T−0 on it', () => {
  assert.equal(milestoneOffset(-14), 'T−14')
  assert.equal(milestoneOffset(7), 'T+7')
  assert.equal(milestoneOffset(0), 'T−0')
  assert.equal(milestoneSentence(-1), '1 day before the target')
  assert.equal(milestoneSentence(-14), '14 days before the target')
  assert.equal(milestoneSentence(1), '1 day after the target')
  assert.equal(milestoneSentence(0), 'On the target date')
})

test('counts agree with their noun', () => { assert.equal(count(1, 'task'), '1 task'); assert.equal(count(0, 'task'), '0 tasks'); assert.equal(count(3, 'milestone'), '3 milestones') })

test('a version is named by its template and number', () => assert.equal(versionLabel({ name: 'Quarterly', versionNo: 2 }), 'Quarterly · version 2'))

test('tasks are listed as one table and stay on their gates', () => {
  const lines = taskLines(gates())
  assert.deepEqual(lines.map(l => [l.gateIndex, l.taskIndex, l.gateName, l.task.taskDescription]), [[0, 0, 'Code Freeze', 'Tag repos'], [0, 1, 'Code Freeze', 'Freeze branch']])
  const added = addTask(gates(), 1)
  assert.equal(added[1].tasks?.length, 1)
  assert.equal(added[0].tasks?.length, 2)
  assert.equal(gates()[1].tasks?.length, 0)   // the originals are not changed
  assert.equal(updateTask(added, 1, 0, { taskDescription: 'SOX evidence' })[1].tasks?.[0].taskDescription, 'SOX evidence')
  assert.deepEqual(removeTask(gates(), 0, 0)[0].tasks?.map(t => t.taskDescription), ['Freeze branch'])
})

test('a task moves to the end of another gate', () => {
  const moved = moveTask(gates(), 0, 0, 1)
  assert.deepEqual(moved[0].tasks?.map(t => t.taskDescription), ['Freeze branch'])
  assert.deepEqual(moved[1].tasks?.map(t => t.taskDescription), ['Tag repos'])
  assert.equal(moveTask(gates(), 0, 0, 0)[0].tasks?.length, 2)
  assert.equal(moveTask(gates(), 0, 9, 1)[1].tasks?.length, 0)
})

test('the editable form of a template keeps the tasks on their gates and drops ids', () => {
  const d = detail()
  d.gates = gates().map((g, i) => ({ ...g, id: `g${i}`, sequenceOrder: i + 1 }))
  const input = toInput(d)
  assert.deepEqual(input.gates[0].tasks, [{ taskDescription: 'Tag repos', ownerTeamId: null }, { taskDescription: 'Freeze branch', ownerTeamId: 'tm1' }])
  assert.equal('id' in input.gates[0], false)
  assert.deepEqual(input.milestones, [{ name: 'Code complete', offsetDays: -14, ownerTeamId: null }])
  assert.deepEqual(blankInput().milestones, [])
})

test('the author does not approve: the screen says so before the button is pressed', () => {
  assert.match(approveBlock(me('u-author', 'ReleaseManager'), detail())!, /You created this template, so someone else approves it/)
  assert.match(approveBlock(me('u-editor', 'ReleaseManager'), detail({}, { lastEditedByUserId: 'u-editor' }))!, /You last changed this template/)
  assert.equal(approveBlock(me('u-other', 'ReleaseManager'), detail()), null)
})

test('a change to a Compliance gate asks for a Governance Officer, in words that fit a first version and a later one', () => {
  const needs = detail({ needsGovernanceApproval: true })
  assert.match(approveBlock(me('u-other', 'ReleaseManager'), needs)!, /This version changes a Compliance gate, so a Governance Officer approves it/)
  assert.match(approveBlock(me('u-other', 'ReleaseManager'), detail({ needsGovernanceApproval: true }, { supersedesTemplateId: null }))!, /This template has Compliance gates/)
  assert.equal(approveBlock(me('u-gov', 'GovernanceOfficer'), needs), null)
  assert.match(approveBlock(me('u-author', 'GovernanceOfficer'), needs)!, /You created/)   // separation of duties comes first, as on the server
})

test('nothing blocks an approval that is not a draft', () => assert.equal(approveBlock(me('u-author', 'ReleaseManager'), detail({}, { status: 'Approved' })), null))

test('Save says what is missing, in words', () => {
  const ok = blankInput(); ok.name = 'Monthly'; ok.gates[0].gateName = 'Code Freeze'
  assert.equal(draftProblem(ok), null)
  assert.equal(draftProblem({ ...ok, name: ' ' }), 'the template has no name')
  assert.equal(draftProblem({ ...ok, gates: [{ ...ok.gates[0], gateName: '' }] }), 'gate 1 has no name')
  assert.equal(draftProblem({ ...ok, gates: [ok.gates[0], { ...ok.gates[0], gateName: 'code freeze' }] }), 'gate 2 repeats the name code freeze')
  assert.equal(draftProblem({ ...ok, gates: [{ ...ok.gates[0], tasks: [{ taskDescription: ' ', ownerTeamId: null }] }] }), 'task 1 of gate 1 has no description')
  assert.equal(draftProblem({ ...ok, milestones: [{ name: '', offsetDays: -3, ownerTeamId: null }] }), 'milestone 1 has no name')
})
