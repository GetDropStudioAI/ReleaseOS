// REOS-183 (D58): the AI settings panel's words and controls. The key is never part of the status; controls are buttons with confirmation, never toggles.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { actionIntro, actionIsRestrictive, actionLabel, callLabel, draftingLabel, globalLabel, keyLabel, optOutAction, pauseAction, smallLabel, type AiAction, type AiStatus } from '../src/mgmt/aiLogic.ts'

const status = (over: Partial<AiStatus> = {}): AiStatus => ({
  moduleEnabled: true, moduleEntitled: true, provider: 'anthropic', keyPresent: false, keyRequired: true, globalKillSwitch: false, workspacePaused: false, workspaceOptedOut: false,
  models: { small: 'small-x', drafting: null, draftingFallsBackToSmall: true }, features: [], timeoutSeconds: 30, maxRetries: 2, canCall: false, blockedBecause: 'No provider key is stored', ...over,
})

test('the key is described in words and the status type has no field that could carry it', () => {
  assert.equal(keyLabel(status()), 'Not set')
  assert.equal(keyLabel(status({ keyPresent: true })), 'Stored')
  assert.equal(keyLabel(status({ keyRequired: false })), 'Not needed (test provider)')
  assert.ok(!Object.keys(status()).some(k => /^(key|apiKey|secret)$/i.test(k)))
})

test('the kill switches and the call line say what is happening and why', () => {
  assert.match(globalLabel(status({ globalKillSwitch: true })), /^On/)
  assert.equal(globalLabel(status()), 'Off')
  assert.match(callLabel(status()), /No provider key is stored/)
  assert.equal(callLabel(status({ canCall: true, blockedBecause: null })), 'AI calls are allowed')
})

test('models: drafting falls back to the small model in words, and a missing one says so', () => {
  assert.equal(smallLabel(status()), 'small-x')
  assert.equal(draftingLabel(status()), 'small-x (same as small)')
  assert.equal(draftingLabel(status({ models: { small: 's', drafting: 'big', draftingFallsBackToSmall: false } })), 'big')
  assert.equal(smallLabel(status({ models: { small: null, drafting: null, draftingFallsBackToSmall: true } })), 'Not configured')
})

test('each control flips with the state and every action has a label, an explanation and no toggle wording', () => {
  assert.equal(pauseAction(status()), 'pause')
  assert.equal(pauseAction(status({ workspacePaused: true })), 'resume')
  assert.equal(optOutAction(status()), 'opt-out')
  assert.equal(optOutAction(status({ workspaceOptedOut: true })), 'opt-in')
  for (const a of ['pause', 'resume', 'opt-out', 'opt-in'] as AiAction[]) {
    assert.ok(actionLabel[a].length > 0 && actionIntro[a].length > 0)
    assert.ok(!/toggle|switch (on|off)/i.test(actionLabel[a]))
  }
  assert.ok(actionIsRestrictive('pause') && actionIsRestrictive('opt-out') && !actionIsRestrictive('resume') && !actionIsRestrictive('opt-in'))
})
