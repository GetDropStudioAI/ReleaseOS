// REOS-156: the Environments screen's pure parts: the table's order, the Add form's reasons, when archiving is off, how an SLO figure reads, and what the signing-key panel offers.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  addProblem, archiveBlock, budgetLine, conflictText, keyFacts, ms, pct, signingState, sloLine, sloWord, sortEnvironments, stateTone, stateWord,
  type EnvironmentRow, type SigningKeyResult,
} from '../src/environments/envLogic.ts'

const env = (o: Partial<EnvironmentRow> = {}): EnvironmentRow => ({
  id: 'e1', name: 'Production EU', region: 'eu-west-1', kind: 'Production', sortOrder: 10, controlWebhook: null, errorRateMaxPct: null, latencyP95MaxMs: null, availabilityMinPct: null,
  sloAvailabilityPct: null, sloLatencyP95Ms: null, sloErrorRatePct: null, archived: false, activeDeployments: 0, version: 1, thresholdForSeconds: 120, canaryObservationSeconds: 600,
  controlWebhookSigned: false, ...o,
})
const hook = { id: 'w1', name: 'Deploy tool', host: 'deploy.example.com' }

test('the table lists active environments first, in the workspace order, then by name', () => {
  const rows = [env({ id: 'a', name: 'Zed', sortOrder: 10, archived: true }), env({ id: 'b', name: 'Beta', sortOrder: 20 }), env({ id: 'c', name: 'Alpha', sortOrder: 20 }), env({ id: 'd', name: 'First', sortOrder: 10 })]
  assert.deepEqual(sortEnvironments(rows).map(r => r.id), ['d', 'c', 'b', 'a'])
  assert.deepEqual(rows.map(r => r.id), ['a', 'b', 'c', 'd'], 'the input is not reordered')
})

test('state is a word and a tone: archived is neutral, active is success', () => {
  assert.equal(stateWord(env()), 'Active'); assert.equal(stateTone(env()), 'success')
  assert.equal(stateWord(env({ archived: true })), 'Archived'); assert.equal(stateTone(env({ archived: true })), 'neutral')
})

test('Add says why it is off, in words', () => {
  const existing = [{ name: 'Staging US' }]
  assert.equal(addProblem('  ', 'Staging', '', existing), 'Enter a name for the environment.')
  assert.match(addProblem('x'.repeat(101), 'Staging', '', existing)!, /at most 100/)
  assert.equal(addProblem('Dev', 'Qa', '', existing), 'Choose Staging or Production.')
  assert.match(addProblem('Dev', 'Staging', 'r'.repeat(61), existing)!, /at most 60/)
  assert.equal(addProblem(' staging us ', 'Staging', '', existing), 'An environment called "staging us" exists.')
  assert.equal(addProblem('Dev', 'Production', '', existing), null)
})

test('an environment with deployments in progress cannot be archived, and says how many', () => {
  assert.equal(archiveBlock(env()), null)
  assert.match(archiveBlock(env({ activeDeployments: 1 }))!, /has 1 deployment in progress/)
  assert.match(archiveBlock(env({ activeDeployments: 3 }))!, /has 3 deployments in progress/)
  assert.equal(archiveBlock(env({ archived: true, activeDeployments: 3 })), null, 'restoring is never blocked')
})

test('an SLO figure is a word as well as a tone', () => {
  assert.deepEqual(sloWord({ objective: null, measured: 99, meets: null }), { word: 'No target', tone: 'neutral' })
  assert.deepEqual(sloWord({ objective: 99.9, measured: null, meets: null }), { word: 'No data', tone: 'neutral' })
  assert.deepEqual(sloWord({ objective: 99.9, measured: 99.95, meets: true }), { word: 'Meets target', tone: 'success' })
  assert.deepEqual(sloWord({ objective: 99.9, measured: 99.5, meets: false }), { word: 'Misses target', tone: 'danger' })
})

test('figures read with their unit and the direction of the target', () => {
  assert.equal(pct(99.95), '99.95%'); assert.equal(pct(99.9000001), '99.9%'); assert.equal(pct(null), 'Not set')
  assert.equal(ms(312.4), '312 ms'); assert.equal(ms(null), 'Not set')
  assert.equal(sloLine({ objective: 99.9, measured: 99.95, meets: true }, v => pct(v), 'higher'), 'Measured 99.95%; target 99.9% or more')
  assert.equal(sloLine({ objective: 300, measured: 412, meets: false }, ms, 'lower'), 'Measured 412 ms; target 300 ms or less')
  assert.equal(sloLine({ objective: 99.9, measured: null, meets: null }, v => pct(v), 'higher'), 'Target 99.9% or more; no samples in this range')
  assert.equal(sloLine({ objective: null, measured: null, meets: null }, v => pct(v), 'higher'), 'Measured no data; no target set')
})

test('the error budget reads as what remains, or that it is exhausted, or why there is none', () => {
  assert.match(budgetLine(null), /needs an availability target/)
  assert.equal(budgetLine({ objectivePct: 99.9, measuredPct: 99.95, meets: true, budgetPct: 0.1, consumedPct: 50, remainingPct: 50, exhausted: false }), '50% of the 0.1% budget remains.')
  assert.equal(budgetLine({ objectivePct: 99.9, measuredPct: 99.5, meets: false, budgetPct: 0.1, consumedPct: 500, remainingPct: 0, exhausted: true }), 'Exhausted: 500% of the 0.1% budget used.')
})

test('the signing key panel: nothing without a webhook or the role; create, then rotate once a key exists', () => {
  assert.deepEqual(signingState(env(), true), { word: 'No control webhook', tone: 'neutral', action: 'Create signing key', off: 'Choose a control webhook for Production EU first.' })
  assert.deepEqual(signingState(env({ controlWebhook: hook }), true), { word: 'No key', tone: 'warning', action: 'Create signing key', off: null })
  assert.deepEqual(signingState(env({ controlWebhook: hook, controlWebhookSigned: true }), true), { word: 'Key set', tone: 'success', action: 'Rotate signing key', off: null })
  assert.match(signingState(env({ controlWebhook: hook }), false).off!, /Only an RTE or Release Manager/)
})

test('the one-time key is shown with the facts a receiver needs, and the secret is not among the labelled facts', () => {
  const k: SigningKeyResult = { secret: 'S3CR3T-VALUE', header: 'X-Sig', deliveryHeader: 'X-Delivery', timestampHeader: 'X-Ts', scheme: 'v1=hex(HMAC)', toleranceSeconds: 300, note: 'Shown once.' }
  const facts = keyFacts(k)
  assert.deepEqual(facts.map(f => f.label), ['Signature header', 'Delivery id header', 'Timestamp header', 'Signature', 'Timestamp tolerance'])
  assert.equal(facts[4].value, '300 seconds')
  assert.ok(!JSON.stringify(facts).includes('S3CR3T'))
})

test('a 409 message names the environment', () => { assert.match(conflictText('Production EU'), /Someone else changed Production EU/) })
