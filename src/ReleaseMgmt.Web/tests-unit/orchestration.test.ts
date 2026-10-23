// REOS-98 / REOS-128: the pure parts of the Orchestration screen: stage cards, step states, durations, the run clock, the bars, and the routes that lead into it.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import type { RunStepRow } from '../src/api.ts'
import type { StageSummary } from '../src/orchestration/orchApi.ts'
import { axisX, bars, clockOf, defaultStage, doneCodesOf, duration, elapsed, lastEvent, orderedStages, runKey, runWord, stageCards, stepSeconds, stepState, strategyFor } from '../src/orchestration/orchLogic.ts'
import { buildPath, normalize, parsePath, ROOT } from '../src/route.ts'

const T = (hm: string) => `2026-10-23T${hm}:00Z`
const step = (code: string, section: string, status: string, over: Partial<RunStepRow> = {}): RunStepRow => ({
  stepId: `id-${code}`, stepCode: code, section, title: `Step ${code}`, ownerName: 'Maya', plannedStartAt: T('14:00'), plannedEndAt: T('14:10'), plannedDurationMin: 10, status, actualStartAt: null, actualEndAt: null,
  actorName: null, note: null, dependsOn: [], blocks: [], instructions: null, canAct: true, version: 1, ...over })
const stage = (section: string, status: StageSummary['status'], over: Partial<StageSummary> = {}): StageSummary => ({
  section, order: 0, steps: 3, scheduled: 0, running: 0, done: 0, failed: 0, skipped: 0, status, plannedStartAt: T('14:00'), plannedEndAt: T('14:30'), actualStartAt: null, actualEndAt: null, startDelayMin: null, ...over })
const run = (stages: StageSummary[], over: Partial<{ paused: boolean; pauseReason: string | null; endedAt: string | null }> = {}) => ({ paused: false, pauseReason: null, endedAt: null, stages, ...over })
const NOW = Date.parse(T('14:32'))

test('a run key is RUN- and the last six characters of the id; times are UTC; durations read as the design writes them', () => {
  assert.equal(runKey('0199-aaaa-bbbb-cc12ab'), 'RUN-CC12AB')
  assert.equal(elapsed(32 * 60 + 8), '32m 08s')
  assert.equal(elapsed(3600 + 2 * 60 + 8), '1h 02m 08s')
  assert.equal(elapsed(-5), '0m 00s')
  assert.equal(duration(68), '01m 08s')
})

test('stages are ordered the way a run reads, with Rollback last, and any number of them is fine', () => {
  const order = orderedStages([stage('Rollback', 'Pending'), stage('Verify', 'Pending'), stage('PreCheck', 'Done'), stage('Hypercare', 'Pending'), stage('Deploy', 'Running')]).map(s => s.section)
  assert.deepEqual(order, ['PreCheck', 'Deploy', 'Verify', 'Hypercare', 'Rollback'])
  assert.equal(stageCards(run([stage('Deploy', 'Running', { running: 1 })]), [step('D1', 'Deploy', 'Running')], NOW).length, 1)
  assert.equal(stageCards(run([]), [], NOW).length, 0)
})

test('a card says its status in a word, with the tone the word has, and counts its steps', () => {
  const steps = [step('P1', 'PreCheck', 'Done', { actualStartAt: T('14:00'), actualEndAt: T('14:02') }), step('D1', 'Deploy', 'Running', { actualStartAt: T('14:08') }), step('D2', 'Deploy', 'Scheduled')]
  const cards = stageCards(run([stage('PreCheck', 'Done', { steps: 1, done: 1, actualStartAt: T('14:00'), actualEndAt: T('14:02') }), stage('Deploy', 'Running', { steps: 2, running: 1, scheduled: 1, actualStartAt: T('14:08') }), stage('Verify', 'Pending')]), steps, NOW)
  assert.deepEqual(cards.map(c => [c.word, c.tone]), [['Successful', 'success'], ['Running', 'info'], ['Planned', 'neutral']])
  assert.equal(cards[0].summary, '1 / 1 steps done')
  assert.equal(cards[1].summary, '1 running · 1 queued')
  assert.equal(cards[1].span, '14:08 → now UTC · 24m')
  assert.equal(cards[0].span, '14:00 → 14:02 UTC · 2m')
  assert.match(cards[1].announce, /^Deploy, Running\./)
  assert.deepEqual(cards[0].bullets.map(b => b.word), ['done'])
})

test('a stage that has not started is Blocked while the run is paused or an earlier stage failed, and says why; Rollback is never Blocked', () => {
  const steps = [step('D1', 'Deploy', 'Failed'), step('V1', 'Verify', 'Scheduled'), step('R1', 'Rollback', 'Scheduled')]
  const stages = [stage('Deploy', 'Failed', { steps: 1, failed: 1 }), stage('Verify', 'Pending', { steps: 1, scheduled: 1 }), stage('Rollback', 'Pending', { steps: 1, scheduled: 1 })]
  const failed = stageCards(run(stages), steps, NOW)
  assert.deepEqual(failed.map(c => c.word), ['Failed', 'Blocked', 'Planned'])
  assert.ok(failed[1].bullets.some(b => /D1 failed/.test(b.text)))
  assert.equal(failed[2].summary, 'Only if called')
  const paused = stageCards(run([stage('Deploy', 'Done', { done: 3 }), stage('Verify', 'Pending')], { paused: true, pauseReason: 'Waiting on SRE' }), [], NOW)
  assert.equal(paused[1].word, 'Blocked')
  assert.ok(paused[1].bullets[0].text.includes('Waiting on SRE'))
  const over = stageCards(run([stage('Verify', 'Pending')], { endedAt: T('15:00') }), [], NOW)
  assert.equal(over[0].word, 'Skipped')
})

test('the stage selected by default is the one that is running or failed, else the first not done', () => {
  const cards = (ws: [string, StageSummary['status']][]) => stageCards(run(ws.map(([s, st]) => stage(s, st))), [], NOW)
  assert.equal(defaultStage(cards([['PreCheck', 'Done'], ['Deploy', 'Running'], ['Verify', 'Pending']])), 'Deploy')
  assert.equal(defaultStage(cards([['PreCheck', 'Done'], ['Deploy', 'Pending']])), 'Deploy')
  assert.equal(defaultStage(cards([['PreCheck', 'Done'], ['Deploy', 'Done'], ['Rollback', 'Pending']])), 'Rollback')
  assert.equal(defaultStage([]), null)
})

test('a step is Queued until it has been started; it names what it still waits for', () => {
  const steps = [step('A', 'Deploy', 'Done'), step('B', 'Deploy', 'Scheduled', { dependsOn: ['A'] }), step('C', 'Deploy', 'Scheduled', { dependsOn: ['B'] }), step('D', 'Deploy', 'Skipped'), step('E', 'Deploy', 'Failed')]
  const done = doneCodesOf(steps)
  assert.deepEqual(steps.map(s => stepState(s, done).word), ['Successful', 'Queued', 'Queued', 'Skipped', 'Failed'])
  assert.deepEqual(stepState(steps[2], done).waitingOn, ['B'])
  assert.deepEqual(stepState(steps[1], done).waitingOn, [])
  assert.equal(stepState(steps[4], done).tone, 'danger')
})

test('a step takes its whole time once it has ended, so far while it runs, and nothing before it starts', () => {
  assert.equal(stepSeconds(step('A', 'Deploy', 'Done', { actualStartAt: T('14:08'), actualEndAt: T('14:09') }), NOW), 60)
  assert.equal(stepSeconds(step('A', 'Deploy', 'Running', { actualStartAt: T('14:22') }), NOW), 600)
  assert.equal(stepSeconds(step('A', 'Deploy', 'Scheduled'), NOW), null)
})

test('the strategy comes from the run\'s deployment whose service the step names, and is blank when none does', () => {
  const d = { serviceKey: 'checkout-api', serviceName: 'Checkout API', strategy: 'Canary', state: 'Running', trafficPercent: 25 } as never
  const rolling = { serviceKey: 'cart-service', serviceName: 'Cart service', strategy: 'Rolling', state: 'Successful', trafficPercent: 100 } as never
  assert.equal(strategyFor({ title: 'Deploy checkout-api v3.18.0' }, [d, rolling]), 'Canary · 25%')
  assert.equal(strategyFor({ title: 'Deploy Cart service' }, [d, rolling]), 'Rolling')
  assert.equal(strategyFor({ title: 'Smoke tests' }, [d, rolling]), null)
})

test('the last event is the newest start or end of any step', () => {
  assert.equal(lastEvent([step('A', 'Deploy', 'Scheduled')]), null)
  const e = lastEvent([step('A', 'Deploy', 'Done', { actualStartAt: T('14:00'), actualEndAt: T('14:05') }), step('B', 'Deploy', 'Running', { actualStartAt: T('14:10') })])
  assert.deepEqual(e, { at: Date.parse(T('14:10')), text: 'B started' })
  assert.equal(lastEvent([step('A', 'Deploy', 'Failed', { actualStartAt: T('14:00'), actualEndAt: T('14:05') })])?.text, 'A failed')
})

test('the clock counts down from the forecast\'s own time, flags the last 30 minutes and a passed deadline', () => {
  const fc = { asOf: T('14:30'), windowClosesInSec: 3600, rollbackDeadline: T('15:00') }
  const c = clockOf(fc, Date.parse(T('14:32')))
  assert.equal(c.windowSec, 3600 - 120)
  assert.equal(c.toDeadlineSec, 28 * 60)
  assert.ok(c.deadlineClose && !c.deadlinePassed)
  assert.ok(clockOf(fc, Date.parse(T('15:01'))).deadlinePassed)
  assert.deepEqual(clockOf(null, NOW), { windowSec: null, toDeadlineSec: null, deadlineClose: false, deadlinePassed: false })
})

test('bars sit on the window-scaled axis: plan always, actual once started, forecast for what remains', () => {
  const win: [number, number] = [Date.parse(T('14:00')), Date.parse(T('16:00'))]
  const running = step('A', 'Deploy', 'Running', { plannedStartAt: T('14:00'), plannedEndAt: T('15:00'), actualStartAt: T('14:30') })
  const b = bars(running, { start: null, end: T('15:30') }, win, Date.parse(T('14:45')), 240)
  assert.deepEqual(b.plan, [0, 120])
  assert.deepEqual(b.actual, [60, 90])
  assert.ok(b.running && b.forecast && b.forecast[0] === 90 && b.forecast[1] === 180 && !b.forecastLate)
  const later = bars(step('B', 'Deploy', 'Scheduled', { plannedStartAt: T('15:00'), plannedEndAt: T('15:30') }), { start: T('15:40'), end: T('16:10') }, win, Date.parse(T('14:45')), 240)
  assert.equal(later.actual, null)
  assert.ok(later.forecastLate && later.forecast![1] === 240)   // clamped to the window
  assert.equal(axisX(Date.parse(T('13:00')), win, 240), 0)
})

test('the run chip says Running, Paused or how the run ended', () => {
  assert.deepEqual(runWord({ paused: false, endedAt: null, outcome: null }), { word: 'Running', tone: 'info' })
  assert.deepEqual(runWord({ paused: true, endedAt: null, outcome: null }), { word: 'Paused', tone: 'warning' })
  assert.equal(runWord({ paused: false, endedAt: T('15:00'), outcome: 'Completed' }).word, 'Successful')
  assert.equal(runWord({ paused: false, endedAt: T('15:00'), outcome: 'RolledBack' }).tone, 'danger')
  assert.equal(runWord({ paused: false, endedAt: T('15:00'), outcome: 'Aborted' }).word, 'Aborted')
})

test('Rehearsal and Live of a release open inside Orchestration, and old links still work', () => {
  assert.equal(buildPath({ view: 'orchestration', trainId: 't1', mode: 'live', selection: null }), '/orchestration/t1/live')
  assert.equal(buildPath({ view: 'orchestration', trainId: 't1', mode: 'rehearsal', selection: { kind: 'step', id: 's9' } }), '/orchestration/t1/rehearsal/steps/s9')
  assert.equal(buildPath({ view: 'orchestration', trainId: null, mode: 'plan', selection: null }), '/orchestration')
  assert.deepEqual(parsePath('/orchestration/t1/live/steps/s9'), { view: 'orchestration', trainId: 't1', mode: 'live', selection: { kind: 'step', id: 's9' } })
  assert.deepEqual(parsePath('/orchestration'), { view: 'orchestration', trainId: null, mode: 'plan', selection: null })
  assert.deepEqual(parsePath('/trains/t1/live'), { view: 'orchestration', trainId: 't1', mode: 'live', selection: null })
  assert.deepEqual(parsePath('/trains/t1/rehearsal/steps/s9'), { view: 'orchestration', trainId: 't1', mode: 'rehearsal', selection: { kind: 'step', id: 's9' } })
  assert.deepEqual(parsePath('/trains/t1'), { view: 'trains', trainId: 't1', selection: null, mode: 'plan' })
  assert.deepEqual(normalize({ ...ROOT, trainId: 't1', mode: 'live', selection: { kind: 'gate', id: 'g' } }), { view: 'orchestration', trainId: 't1', mode: 'live', selection: null })
  assert.deepEqual(normalize({ ...ROOT, trainId: 't1' }), { ...ROOT, trainId: 't1' })
})
