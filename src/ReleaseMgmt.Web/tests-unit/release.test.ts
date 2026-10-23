// REOS-97/126/127: the release detail page's logic: one summary for score, bar and table; the four Go/No-Go states; open decisions; D44 times; unrecorded fields.
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import { NOT_RECORDED, TABS, TAB_LABEL, gateState, goCard, manifestLocked, openDecisions, releaseNotice, releaseProperties, riskChip, stamp, statusChip, summarizeGates, waitingFor, windowStamp } from '../src/release/model.ts'

const gate = (id: string, status: string, done = 0, total = 1) => ({ id, name: `G-${id}`, class: 'Standard', status, dueOn: '2026-11-01', tMinus: 3, requiredBeforeStatus: 'Executing', ownerName: 'Gina', certifiedBy: null, certifiedOn: null, tasksDone: done, tasksTotal: total, version: 1 })
const decision = (d: 'Go' | 'NoGo' | 'GoWithConditions', conds: { id: string; closed: boolean }[] = []) => ({
  decision: { id: 'd1', decision: d, decidedByUserId: 'u1', decidedAt: '2026-10-30T02:00:00Z', notes: 'because', newTargetReleaseDate: null },
  conditions: conds.map(c => ({ id: c.id, decisionId: 'd1', text: `cond ${c.id}`, ownerUserId: 'u1', expiresAt: '2026-11-02T00:00:00Z', closedAt: c.closed ? '2026-10-31T00:00:00Z' : null, closedByUserId: null, version: 1 })),
})

test('score, progress and outstanding come from one summary, so they agree', () => {
  const s = summarizeGates([gate('a', 'Certified', 1, 1), gate('b', 'Waived', 0, 1), gate('c', 'InProgress', 1, 3), gate('d', 'Pending', 0, 2)])
  assert.deepEqual(s, { total: 4, passed: 2, outstanding: 2, ready: 0.5, checksDone: 2, checksTotal: 7 })
  assert.equal(summarizeGates([]).ready, 0)
  assert.equal(summarizeGates([gate('a', 'Certified')]).outstanding, 0)
})

test('gate states use the design words and a pending gate says what it waits for and who owns it', () => {
  assert.equal(gateState('Certified').word, 'Passed')
  assert.equal(gateState('Pending').word, 'Pending')
  assert.equal(gateState('Failed').tone, 'danger')
  assert.match(waitingFor(gate('c', 'InProgress', 1, 3)), /Waiting for 2 of 3 checks; owner Gina/)
  assert.match(waitingFor(gate('d', 'Pending', 2, 2)), /Waiting for Gina to certify/)
  assert.match(waitingFor({ ...gate('e', 'Pending', 0, 0), class: 'Compliance' }), /at least one check/)
})

test('the Go/No-Go card has a distinct label, headline and tone for all four states', () => {
  const cards = [goCard([]), goCard([decision('Go')]), goCard([decision('GoWithConditions', [{ id: 'c1', closed: false }, { id: 'c2', closed: true }])]), goCard([decision('NoGo')])]
  assert.deepEqual(cards.map(c => c.label), ['Not yet decided', 'Go', 'Conditional go', 'No-go'])
  assert.equal(new Set(cards.map(c => c.headline)).size, 4)
  assert.equal(new Set(cards.map(c => c.tone)).size, 4)
  assert.equal(cards[2].openConditions.length, 1)
  assert.match(cards[2].headline, /1 open condition$/)
})

test('open decisions are the gates not passed, the missing Go/No-Go and the open conditions; the tab count is their number', () => {
  const gates = [gate('a', 'Certified'), gate('b', 'Pending')]
  assert.deepEqual(openDecisions(gates, [], 'Gated').map(r => r.key), ['gate:b', 'gonogo'])
  assert.deepEqual(openDecisions(gates, [], 'Complete').map(r => r.key), ['gate:b'])
  assert.deepEqual(openDecisions(gates, [decision('GoWithConditions', [{ id: 'c1', closed: false }, { id: 'c2', closed: true }])], 'Gated').map(r => r.key), ['gate:b', 'cond:c1'])
})

test('times are UTC and labelled, with the local time beside them unless the person works in UTC', () => {
  assert.deepEqual(stamp('2026-10-30T02:00:00Z', 'America/Chicago'), { utc: '30 Oct 2026, 02:00 UTC', local: '29 OCT 21:00 CDT' })
  assert.equal(stamp('2026-10-30T02:00:00Z', 'UTC').local, null)
  assert.equal(windowStamp('2026-11-06T06:00:00Z', '2026-11-06T10:00:00Z', 'UTC').utc, '6 Nov 2026, 06:00 – 10:00 UTC')
  assert.match(windowStamp('2026-11-06T22:00:00Z', '2026-11-07T02:00:00Z', 'UTC').utc, /6 Nov 2026, 22:00 – 7 Nov 2026, 02:00 UTC/)
})

test('fields the app does not record say so instead of inventing a value', () => {
  const props = Object.fromEntries(releaseProperties({ changeTicket: null, backoutPlan: null, rollbackRehearsed: false, riskTier: 'High', cabDate: null, locked: false }).map(p => [p.label, p.value]))
  for (const k of ['Target environments', 'Release branch', 'On-call', 'CAB date']) assert.equal(props[k], NOT_RECORDED, k)
  assert.match(props['Rollback plan'], /required before Executing/)
  assert.equal(props['Service manifest'], 'Open for changes')
  assert.equal(releaseProperties({ changeTicket: 'CHG-1', backoutPlan: 'x', rollbackRehearsed: true, riskTier: 'Low', cabDate: null, locked: true }).find(p => p.label === 'Change record')?.value, 'CHG-1')
})

test('the manifest locks once deployment starts; chips and notice words', () => {
  assert.deepEqual(['Planning', 'Gated', 'Executing', 'Complete', 'Aborted'].map(manifestLocked), [false, false, true, true, true])
  assert.equal(statusChip('Complete', 'Unsuccessful').tone, 'danger')
  assert.equal(riskChip('Moderate').word, 'Medium risk')
  const blocked = releaseNotice({ status: 'Gated', nextStatus: 'Executing', ready: false, guardsMet: 3, guardsTotal: 5, blockers: 2 })
  assert.equal(blocked.review, true); assert.match(blocked.title, /2 checks are blocking Executing/)
  assert.equal(releaseNotice({ status: 'Gated', nextStatus: 'Executing', ready: true, guardsMet: 5, guardsTotal: 5, blockers: 0 }).tone, 'success')
  assert.equal(releaseNotice({ status: 'Aborted', nextStatus: null, ready: null, guardsMet: 0, guardsTotal: 0, blockers: 0 }).tone, 'danger')
})

test('the five tabs are the design\'s, and the release styles use tokens only', () => {
  assert.deepEqual(TABS.map(t => TAB_LABEL[t]), ['Overview & readiness', 'Services', 'Dependencies', 'Approvals', 'Activity'])
  const css = readFileSync(new URL('../src/release/release.css', import.meta.url), 'utf8')
  assert.doesNotMatch(css, /#[0-9a-fA-F]{3,8}\b|rgba?\(|hsla?\(/, 'no hard-coded colour')
})
