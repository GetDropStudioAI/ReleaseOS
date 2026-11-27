import assert from 'node:assert/strict'
import { test } from 'node:test'
import { canDecide, decisionTone, decisionWord, killSwitchSummary, outcomeWord, shortHash, subjectText, tokenText } from '../src/ai/aiInventoryLogic.ts'
import type { AiStatus } from '../src/mgmt/aiLogic.ts'

const status = (o: Partial<AiStatus> = {}): AiStatus => ({
  moduleEnabled: true, moduleEntitled: true, provider: 'fake', keyPresent: false, keyRequired: false, globalKillSwitch: false, workspacePaused: false, workspaceOptedOut: false,
  models: { small: 'small-model', drafting: null, draftingFallsBackToSmall: true }, features: [], timeoutSeconds: 30, maxRetries: 2, canCall: true, blockedBecause: null, ...o,
})

test('every decision is a word, and the tone repeats it', () => {
  assert.equal(decisionWord('Pending'), 'Awaiting decision')
  assert.equal(decisionWord('Edited'), 'Edited and accepted')
  assert.equal(decisionWord('NotApplicable'), 'No draft')
  assert.equal(decisionTone('Pending'), 'warning')
  assert.equal(decisionTone('Rejected'), 'danger')
  assert.equal(decisionTone('Accepted'), 'success')
})

test('a refusal says which rule refused', () => {
  assert.equal(outcomeWord('KillSwitch'), 'Refused: global kill switch')
  assert.equal(outcomeWord('Blocked'), 'Refused: data rule')
  assert.equal(outcomeWord('Surprise'), 'Surprise')
})

test('the kill switch summary names the first switch that is on, global first', () => {
  assert.match(killSwitchSummary(status({ globalKillSwitch: true, workspacePaused: true })), /global kill switch is on/)
  assert.match(killSwitchSummary(status({ moduleEnabled: false, canCall: false })), /module is off/)
  assert.match(killSwitchSummary(status({ workspacePaused: true, canCall: false })), /paused/)
  assert.match(killSwitchSummary(status({ workspaceOptedOut: true, canCall: false })), /opted out/)
  assert.match(killSwitchSummary(status()), /No kill switch is on/)
})

test('only the requester or an administrator can decide a pending draft', () => {
  const draft = { decision: 'Pending', outcome: 'Completed', requestedBy: { id: 'u1', name: 'Una' } }
  assert.equal(canDecide(draft, { id: 'u1', roles: ['Viewer'] }), true)
  assert.equal(canDecide(draft, { id: 'u2', roles: ['Viewer'] }), false)
  assert.equal(canDecide(draft, { id: 'u2', roles: ['RTE'] }), true)
  assert.equal(canDecide(draft, { id: 'u2', roles: ['GovernanceOfficer'] }), false)
  assert.equal(canDecide({ ...draft, decision: 'Accepted' }, { id: 'u1', roles: ['RTE'] }), false)
  assert.equal(canDecide({ ...draft, outcome: 'KillSwitch' }, { id: 'u1', roles: ['RTE'] }), false)
})

test('small helpers', () => {
  assert.equal(shortHash(null), 'none')
  assert.equal(shortHash('a'.repeat(64)), 'aaaaaaaaaaaa…')
  assert.equal(subjectText({ subjectType: 'Train', subjectId: 'r1' }), 'Train r1')
  assert.equal(subjectText({ subjectType: null, subjectId: null }), 'None')
  assert.equal(tokenText(1200, 30), '1,200 in, 30 out')
})
