import assert from 'node:assert/strict'
import test from 'node:test'
import { readFileSync } from 'node:fs'
import {
  ALL_EVENTS, PAUSED_REASON, allowListNotice, attemptText, attemptsText, deleteImpact, deliveryFooter, deliveryTone, deliveryWord, eventsText, footerText, formProblem, normaliseChoice,
  prettyPayload, queueText, replayReason, resultText, rotateImpact, sortDeliveries, sortSubscriptions, statusTone, type Delivery, type Subscription, type WebhookEvent,
} from '../src/webhooks/webhooksLogic.ts'

// REOS-164: the pure parts of the Webhooks screen.
const catalogue: WebhookEvent[] = [
  { key: 'release.created', name: 'Release created', description: '' }, { key: 'gate.failed', name: 'Gate failed', description: '' }, { key: 'gate.certified', name: 'Gate certified', description: '' },
]
const sub = (o: Partial<Subscription> = {}): Subscription => ({ id: 's1', name: 'Dashboard', host: 'hooks.example.com', eventTypes: ['*'], status: 'Active', secretVersion: 1, hasSecret: true, secretRotatedAt: null,
  createdAt: '2026-11-26T12:00:00Z', createdBy: 'Rita', lastChangedAt: null, pending: 0, deadLetters: 0, version: 1, ...o })
const del = (o: Partial<Delivery> = {}): Delivery => ({ id: 'd1', subscriptionId: 's1', subscriptionName: 'Dashboard', eventId: 'evt_1', eventType: 'gate.failed', status: 'Pending', attemptCount: 0, nextAttemptAt: '2026-11-26T12:01:00Z',
  lastAttemptAt: null, lastStatusCode: null, lastError: null, deliveredAt: null, finishedAt: null, replayOfDeliveryId: null, createdAt: '2026-11-26T12:00:00Z', version: 1, ...o })

test('events are said in words: all, the names, or the first two and a count', () => {
  assert.equal(eventsText(sub(), catalogue), 'All events')
  assert.equal(eventsText(sub({ eventTypes: ['release.created'] }), catalogue), 'Release created')
  assert.equal(eventsText(sub({ eventTypes: ['release.created', 'gate.failed'] }), catalogue), 'Release created, Gate failed')
  assert.equal(eventsText(sub({ eventTypes: ['release.created', 'gate.failed', 'gate.certified'] }), catalogue), 'Release created, Gate failed and 1 more')
  assert.equal(eventsText(sub({ eventTypes: ['x.unknown'] }), catalogue), 'x.unknown')
})

test('All events on its own wins over any other choice', () => {
  assert.deepEqual(normaliseChoice(['gate.failed', ALL_EVENTS]), [ALL_EVENTS])
  assert.deepEqual(normaliseChoice(['gate.failed', 'release.created']), ['gate.failed', 'release.created'])
})

test('the form says why it cannot be sent, in words, and an edit may leave the address empty', () => {
  assert.equal(formProblem('', 'https://a.example.com', ['*']), 'Enter a name.')
  assert.match(formProblem('x'.repeat(101), 'https://a.example.com', ['*'])!, /at most 100/)
  assert.match(formProblem('CI', '', ['*'])!, /Enter the address/)
  assert.match(formProblem('CI', 'http://a.example.com', ['*'])!, /https/)
  assert.equal(formProblem('CI', 'https://a.example.com', []), 'Choose at least one event.')
  assert.equal(formProblem('CI', 'https://a.example.com', ['*']), null)
  assert.equal(formProblem('CI', '', ['gate.failed'], false), null)
  assert.match(formProblem('CI', 'ftp://a.example.com', ['*'], false)!, /https/)
})

test('status words and tones: never colour alone, and a failed pending delivery is Retrying', () => {
  assert.equal(statusTone('Active'), 'success'); assert.equal(statusTone('Paused'), 'warning')
  assert.equal(deliveryTone('Delivered'), 'success'); assert.equal(deliveryTone('DeadLetter'), 'danger'); assert.equal(deliveryTone('Pending'), 'warning')
  assert.equal(deliveryWord(del()), 'Pending')
  assert.equal(deliveryWord(del({ attemptCount: 2 })), 'Retrying')
  assert.equal(deliveryWord(del({ status: 'DeadLetter', attemptCount: 10 })), 'Dead letter')
  assert.equal(deliveryWord(del({ status: 'Delivered', attemptCount: 1 })), 'Delivered')
})

test('the last result and the attempts read as words', () => {
  assert.equal(resultText(del()), 'Not tried yet')
  assert.equal(resultText(del({ attemptCount: 1, lastStatusCode: 500, lastError: 'HTTP 500' })), 'HTTP 500')
  assert.equal(resultText(del({ attemptCount: 1, lastError: 'no answer within 10s' })), 'no answer within 10s')
  assert.equal(resultText(del({ attemptCount: 1 })), 'No answer')
  assert.equal(resultText(del({ status: 'Delivered', attemptCount: 1, lastStatusCode: 200 })), 'Delivered (HTTP 200)')
  assert.equal(attemptText({ statusCode: 502, error: null }), 'HTTP 502'); assert.equal(attemptText({ statusCode: null, error: null }), 'No answer')
  assert.equal(attemptsText(1), '1 attempt'); assert.equal(attemptsText(3), '3 attempts')
})

test('the queue, the footers and the sort', () => {
  assert.equal(queueText(sub()), 'Nothing waiting')
  assert.equal(queueText(sub({ pending: 2 })), '2 pending')
  assert.equal(queueText(sub({ pending: 2, deadLetters: 1 })), '2 pending · 1 dead-lettered')
  assert.equal(footerText([sub(), sub({ id: 's2', status: 'Paused' })]), '2 subscriptions · 1 active')
  assert.equal(footerText([sub()]), '1 subscription · 1 active')
  assert.equal(deliveryFooter([del(), del({ id: 'd2', status: 'DeadLetter' })], 100), '2 deliveries · 1 dead-lettered')
  assert.equal(deliveryFooter([del()], 1), '1 delivery (the latest 1) · 0 dead-lettered')
  assert.deepEqual(sortDeliveries([del({ id: 'a', createdAt: '2026-11-26T12:00:00Z' }), del({ id: 'b', createdAt: '2026-11-26T13:00:00Z' })]).map(d => d.id), ['b', 'a'])
  assert.deepEqual(sortSubscriptions([sub({ id: '2', name: 'Zed' }), sub({ id: '1', name: 'Alpha' })]).map(s => s.name), ['Alpha', 'Zed'])
})

test('the allow-list notice says what is allowed and what to do when nothing is', () => {
  assert.equal(allowListNotice({ mode: 'closed', hosts: [], allowPrivateTargets: false }).tone, 'warning')
  assert.match(allowListNotice({ mode: 'closed', hosts: [], allowPrivateTargets: false }).text, /Webhooks:Out:AllowedHosts/)
  assert.match(allowListNotice({ mode: 'restricted', hosts: ['a.example.com', '*.b.example.org'], allowPrivateTargets: false }).text, /a\.example\.com, \*\.b\.example\.org/)
  assert.match(allowListNotice({ mode: 'open', hosts: [], allowPrivateTargets: false }).title, /Any public host/)
})

test('replay is off for a pending delivery, a deleted webhook and a paused one, each with its reason', () => {
  assert.equal(replayReason(del({ status: 'DeadLetter' }), sub()), null)
  assert.equal(replayReason(del({ status: 'Delivered' }), sub()), null)
  assert.match(replayReason(del({ status: 'Pending' }), sub())!, /still being tried/)
  assert.match(replayReason(del({ status: 'DeadLetter' }), undefined)!, /deleted/)
  assert.equal(replayReason(del({ status: 'DeadLetter' }), sub({ status: 'Paused' })), PAUSED_REASON)
})

test('the confirmations say what happens', () => {
  assert.match(rotateImpact('Dashboard'), /stops verifying at once/)
  assert.match(rotateImpact('Dashboard'), /shown once/)
  assert.match(deleteImpact({ name: 'Dashboard', pending: 0 }), /cannot be undone/)
  assert.match(deleteImpact({ name: 'Dashboard', pending: 3 }), /3 pending deliveries are dropped/)
  assert.match(deleteImpact({ name: 'Dashboard', pending: 1 }), /1 pending delivery is dropped/)
})

test('a payload is shown pretty and a body that is not JSON is shown as it is', () => {
  assert.equal(prettyPayload('{"a":1}'), '{\n  "a": 1\n}')
  assert.equal(prettyPayload('not json'), 'not json')
})

test('the screen uses tokens only and no checkbox or switch', () => {
  const css = readFileSync(new URL('../src/webhooks/webhooks.css', import.meta.url), 'utf8')
  assert.doesNotMatch(css, /#[0-9a-fA-F]{3,8}\b|rgba?\(|hsla?\(/)
  const tsx = readFileSync(new URL('../src/webhooks/Webhooks.tsx', import.meta.url), 'utf8')
  assert.doesNotMatch(tsx, /type="checkbox"|role="switch"|type="radio"/)
  assert.doesNotMatch(tsx, /#[0-9a-fA-F]{6}\b/)
})
