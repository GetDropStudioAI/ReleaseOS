// REOS-100 / REOS-140: the pure parts of the Deployments screens: the key, the range, chart scales, and the words under each metric card and in the rollback banner.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { Monitoring } from '../src/deploy/deployApi.ts'
import {
  anchorOf, availabilityCard, depKey, errorCard, healthOf, latencyCard, niceMax, pendingControl, pointsOf, rangeStart, rollbackBanner, stampOf, stateTone, stateWord, timeTicks, trafficCard, utcHM, utcHMS, yMax,
} from '../src/deploy/deployLogic.ts'

const dep = { id: '0199-aaaa-bbbb-cc12ab', externalId: 'ext-1', serviceId: 's', serviceKey: 'checkout-api', serviceName: 'Checkout API', environmentId: 'e', environmentName: 'Production EU', environmentKind: 'Production',
  trainId: 't', releaseKey: 'RLS-284', trainTitle: 'Checkout', runId: null, releaseVersion: 'v3.18.0', strategy: 'Canary', canarySteps: [25, 50, 100], state: 'Running', trafficPercent: 25, readyPods: 24, desiredPods: 96,
  startedAt: '2026-10-23T14:00:00Z', finishedAt: null, lastEventAt: null, createdAt: '', updatedAt: '', version: 3, stepStartedAt: null, autoRollbackArmed: true }
const mon = (over: Partial<Monitoring> = {}): Monitoring => ({
  deployment: dep, thresholds: { errorRateMaxPct: 0.5, latencyP95MaxMs: 300, availabilityMinPct: 99.5, forSeconds: 120 }, slo: { availabilityPct: 99.9, latencyP95Ms: 250, errorRatePct: 0.5 },
  canary: { strategy: 'Canary', isCanary: true, state: 'Running', steps: [25, 50, 100], currentStep: 1, stepCount: 3, trafficPercent: 25, nextTrafficPercent: 50, canaryStartedAt: '2026-10-23T14:00:00Z',
    observationWindowSeconds: 600, observedSeconds: 300, windowComplete: false, nextPromotionAt: '2026-10-23T14:37:00Z', result: 'Pass', atRisk: false, readyToPromote: false, lastSampleAt: null, checks: [], autoRollbackArmed: true },
  breaches: [], rollback: { armed: true, armedAt: null, webhookConfigured: true, signingSecretSet: true, canArm: true, whyNot: null }, actions: [],
  latest: [{ series: 'Canary', at: '2026-10-23T14:32:00Z', errorRatePct: 0.08, latencyP95Ms: 184, availabilityPct: 99.96, requestCount: 100 },
    { series: 'Stable', at: '2026-10-23T14:31:50Z', errorRatePct: 0.05, latencyP95Ms: 196, availabilityPct: 99.97, requestCount: 900 }],
  sloStatus: { days: 30, samples: 10, availability: { objective: 99.9, measured: 99.95, meets: true }, latencyP95: { objective: 250, measured: 190, meets: true }, errorRate: { objective: 0.5, measured: 0.06, meets: true } }, ...over })

test('the key is DEP- and the last six characters of the id; states read as words with a tone', () => {
  assert.equal(depKey('0199-aaaa-bbbb-cc12ab'), 'DEP-CC12AB')
  assert.equal(stateWord('RolledBack'), 'Rolled back')
  assert.deepEqual(['Running', 'Paused', 'Successful', 'Failed', 'RolledBack', 'Queued'].map(stateTone), ['info', 'warning', 'success', 'danger', 'danger', 'neutral'])
})

test('times are UTC and labelled; a person in UTC gets no second clock, anyone else gets theirs', () => {
  assert.equal(utcHM(Date.parse('2026-10-23T14:32:08Z')), '14:32 UTC')
  assert.equal(utcHMS(Date.parse('2026-10-23T14:32:08Z')), '14:32:08')
  assert.deepEqual(stampOf('2026-10-23T14:32:08Z', 'UTC'), { utc: '23 Oct 2026, 14:32 UTC', local: null })
  assert.equal(stampOf('2026-10-23T14:32:08Z', 'America/Chicago').local, '09:32 CDT')
})

test('the range ends at the newest sample, not at the clock, and starts the chosen minutes before it', () => {
  const m = mon()
  assert.equal(anchorOf(m, Date.parse('2030-01-01T00:00:00Z')), Date.parse('2026-10-23T14:32:00Z'))
  assert.equal(rangeStart(Date.parse('2026-10-23T14:32:00Z'), 30), '2026-10-23T14:02:00Z')
  assert.equal(anchorOf(mon({ latest: [] }), 5000), 5000)
  assert.equal(anchorOf(mon({ latest: [], deployment: { ...dep, finishedAt: '2026-10-23T15:00:00Z' } }), 5000), Date.parse('2026-10-23T15:00:00Z'))
})

test('axes round up to a nice number and always leave the threshold on the chart', () => {
  assert.deepEqual([0, 0.3, 1, 1.7, 4.2, 7, 130].map(niceMax), [1, 0.5, 1, 2, 5, 10, 200])
  assert.equal(yMax([[{ t: 0, v: 0.08 }]], 0.5), 1)
  assert.equal(yMax([[{ t: 0, v: 2 }]], 0.5), 2.5)
  assert.deepEqual(timeTicks(0, 30, 4), [0, 10, 20, 30])
  assert.deepEqual(pointsOf([{ at: '2026-10-23T14:00:00Z', errorRatePct: null, latencyP95Ms: 3, availabilityPct: null, requestCount: null }], p => p.errorRatePct), [])
})

test('the four cards say what the numbers mean against the threshold, the stable version and the SLO', () => {
  const m = mon()
  assert.deepEqual(trafficCard(m, 'UTC'), { value: '25%', unit: '24 / 96 pods', detail: 'Next: 50% at 14:37 UTC', tone: 'neutral' })
  assert.deepEqual(errorCard(m), { value: '0.08%', detail: 'Below 0.5% rollback threshold', tone: 'success' })
  assert.deepEqual(latencyCard(m), { value: '184', unit: 'ms', detail: '−12 ms vs. stable version', tone: 'success' })
  assert.deepEqual(availabilityCard(m), { value: '99.96%', detail: 'SLO 99.9% · Met over 30 days', tone: 'success' })
  const bad = mon({ latest: [{ series: 'Canary', at: '2026-10-23T14:32:00Z', errorRatePct: 0.9, latencyP95Ms: 420, availabilityPct: 98, requestCount: null }], sloStatus: { ...m.sloStatus!, availability: { objective: 99.9, measured: 98, meets: false } } })
  assert.equal(errorCard(bad).detail, 'Above 0.5% rollback threshold'); assert.equal(errorCard(bad).tone, 'danger')
  assert.equal(latencyCard(bad).tone, 'danger')
  assert.equal(availabilityCard(bad).detail, 'SLO 99.9% · Not met over 30 days')
  assert.equal(errorCard(mon({ latest: [] })).value, '–')
  assert.equal(trafficCard(mon({ deployment: { ...dep, state: 'Paused' } }), 'UTC').tone, 'warning')
})

test('the rollback banner reads armed and passing, watching, breached, off and rolled back', () => {
  assert.deepEqual(rollbackBanner(mon()), { tone: 'success', title: 'Automatic rollback armed · All SLO gates passing' })
  const breach = (status: 'Watching' | 'Breached') => ({ id: 'b', series: 'Canary', metric: 'ErrorRate', thresholdValue: 0.5, forSeconds: 120, startedAt: '2026-10-23T14:20:00Z', confirmedAt: null, resolvedAt: null, peakValue: 0.9, lastValue: 0.8, status })
  assert.equal(rollbackBanner(mon({ breaches: [breach('Watching')] })).tone, 'warning')
  const hit = rollbackBanner(mon({ breaches: [breach('Breached')] }))
  assert.equal(hit.tone, 'danger'); assert.match(hit.body!, /Error rate 0.8% against 0.5% since 14:20 UTC/)
  const off = rollbackBanner(mon({ rollback: { armed: false, armedAt: null, webhookConfigured: false, signingSecretSet: false, canArm: false, whyNot: 'The environment has no control webhook' } }))
  assert.equal(off.title, 'Automatic rollback is off'); assert.equal(off.body, 'The environment has no control webhook.')
  assert.equal(rollbackBanner(mon({ deployment: { ...dep, state: 'RolledBack' } })).title, 'Rolled back')
  assert.equal(healthOf(mon()), 'Healthy'); assert.equal(healthOf(mon({ breaches: [breach('Breached')] })), 'Failing'); assert.equal(healthOf(mon({ breaches: [breach('Watching')] })), 'At risk')
})

test('a pause the tool has not reported yet is shown as requested until the state follows; a failed call is not', () => {
  const action = (a: 'Pause' | 'Resume', status: 'Pending' | 'Succeeded' | 'Failed') => [{ id: '1', action: a, source: 'Manual', requestedByName: 'Ann', requestedAt: '', reason: null, status, httpStatus: null, error: null, completedAt: null }]
  assert.equal(pendingControl(action('Pause', 'Succeeded'), 'Running')?.action, 'Pause')
  assert.equal(pendingControl(action('Pause', 'Succeeded'), 'Paused'), null)
  assert.equal(pendingControl(action('Resume', 'Succeeded'), 'Paused')?.action, 'Resume')
  assert.equal(pendingControl(action('Pause', 'Pending'), 'Running')?.status, 'Pending')
  assert.equal(pendingControl(action('Pause', 'Failed'), 'Running'), null)
  assert.equal(pendingControl([], 'Running'), null)
})
