import assert from 'node:assert/strict'
import test from 'node:test'
import { readFileSync } from 'node:fs'
import { DOCS, EXPIRING_DAYS, OPENAPI_PATH, TABS, byHealth, healthDetail, overallHealth, quickstart, subscriptionHealth, tokenSummary, webhookSummary } from '../src/integrations/integrationsLogic.ts'
import type { ApiToken } from '../src/tokens/tokensLogic.ts'
import type { Subscription } from '../src/webhooks/webhooksLogic.ts'

// REOS-165: the pure parts of the Integrations page.
const NOW = new Date('2026-11-26T12:00:00Z')
const tok = (o: Partial<ApiToken> = {}): ApiToken => ({ id: 't1', name: 'Build server', tokenPrefix: 'rt_abcd', scopes: ['read'], expiresAt: null, state: 'Active', ownerUserId: 'u1', ownerName: 'Rita', createdAt: '2026-11-01T00:00:00Z',
  rotatedAt: null, lastUsedAt: '2026-11-25T00:00:00Z', revokedAt: null, revokedBy: null, version: 1, ...o })
const sub = (o: Partial<Subscription> = {}): Subscription => ({ id: 's1', name: 'Dashboard', host: 'hooks.example.com', eventTypes: ['*'], status: 'Active', secretVersion: 1, hasSecret: true, secretRotatedAt: null,
  createdAt: '2026-11-26T12:00:00Z', createdBy: 'Rita', lastChangedAt: null, pending: 0, deadLetters: 0, version: 1, ...o })

test('the token summary counts active, expiring within 14 days, expired, revoked and never used, and names the next expiry', () => {
  const in10 = new Date(NOW.getTime() + 10 * 86_400_000).toISOString(), in40 = new Date(NOW.getTime() + 40 * 86_400_000).toISOString()
  const s = tokenSummary([tok({ id: 'a', expiresAt: in10 }), tok({ id: 'b', expiresAt: in40, lastUsedAt: null }), tok({ id: 'c' }), tok({ id: 'd', state: 'Expired', expiresAt: '2026-11-01T00:00:00Z' }), tok({ id: 'e', state: 'Revoked' })], NOW)
  assert.deepEqual(s, { active: 3, expiringSoon: 1, expired: 1, revoked: 1, neverUsed: 1, nextExpiry: in10 })
  assert.equal(EXPIRING_DAYS, 14)
  assert.deepEqual(tokenSummary([], NOW), { active: 0, expiringSoon: 0, expired: 0, revoked: 0, neverUsed: 0, nextExpiry: null })
})

test('a token exactly 14 days from expiry is expiring, one a second later is not; an expired token is never counted as expiring', () => {
  const edge = new Date(NOW.getTime() + 14 * 86_400_000)
  assert.equal(tokenSummary([tok({ expiresAt: edge.toISOString() })], NOW).expiringSoon, 1)
  assert.equal(tokenSummary([tok({ expiresAt: new Date(edge.getTime() + 1000).toISOString() })], NOW).expiringSoon, 0)
  assert.equal(tokenSummary([tok({ state: 'Expired', expiresAt: '2026-11-01T00:00:00Z' })], NOW).expiringSoon, 0)
})

test('webhook health words: Failing outranks Paused outranks Queued outranks Healthy, and each has a tone', () => {
  assert.deepEqual(subscriptionHealth(sub()), { word: 'Healthy', tone: 'success' })
  assert.deepEqual(subscriptionHealth(sub({ pending: 2 })), { word: 'Queued', tone: 'warning' })
  assert.deepEqual(subscriptionHealth(sub({ status: 'Paused', pending: 2 })), { word: 'Paused', tone: 'warning' })
  assert.deepEqual(subscriptionHealth(sub({ status: 'Paused', deadLetters: 1 })), { word: 'Failing', tone: 'danger' })
})

test('the detail line says what is wrong in words', () => {
  assert.equal(healthDetail(sub()), 'No failed or waiting deliveries')
  assert.equal(healthDetail(sub({ deadLetters: 2, pending: 1 })), '2 dead-lettered, 1 waiting to be delivered')
  assert.equal(healthDetail(sub({ status: 'Paused' })), 'no new events are queued while paused')
})

test('webhooks that need a person come first, then by name', () => {
  const order = byHealth([sub({ id: '1', name: 'Zed' }), sub({ id: '2', name: 'Amy', pending: 1 }), sub({ id: '3', name: 'Bob', status: 'Paused' }), sub({ id: '4', name: 'Cy', deadLetters: 3 }), sub({ id: '5', name: 'Abe' })]).map(s => s.id)
  assert.deepEqual(order, ['4', '3', '2', '5', '1'])
})

test('the webhook summary and the overall health line', () => {
  assert.deepEqual(webhookSummary([sub(), sub({ id: '2', status: 'Paused', pending: 4 }), sub({ id: '3', deadLetters: 2 })]), { total: 3, active: 2, paused: 1, pending: 4, deadLetters: 2 })
  assert.deepEqual(overallHealth([]), { value: 'None', detail: 'No webhooks yet', tone: 'neutral' })
  assert.deepEqual(overallHealth([sub()]), { value: 'Healthy', detail: 'Every delivery has been made', tone: 'success' })
  assert.deepEqual(overallHealth([sub({ pending: 3 })]), { value: 'Queued', detail: '3 waiting to be delivered', tone: 'warning' })
  assert.deepEqual(overallHealth([sub({ deadLetters: 1 })]), { value: 'Failing', detail: '1 delivery dead-lettered', tone: 'danger' })
  assert.equal(overallHealth([sub({ deadLetters: 2 })]).detail, '2 deliveries dead-lettered')
})

test('the quickstart reads the token from the environment and never has a secret in it', () => {
  const q = quickstart('https://releases.example.com')
  assert.match(q, /Authorization: Bearer \$RELEASEOS_TOKEN/)
  assert.match(q, /https:\/\/releases\.example\.com\/api\/public\/v1\/me/)
  assert.doesNotMatch(q, /rt_[A-Za-z0-9_-]{20,}/)
})

test('the tabs are the five sections in order, and every document the page points at exists in the repository', () => {
  assert.deepEqual(TABS.map(t => t.id), ['overview', 'tokens', 'webhooks', 'email', 'docs'])
  for (const d of DOCS) {
    const url = new URL(`../../../${d.path}`, import.meta.url)
    assert.doesNotThrow(() => readFileSync(d.path.endsWith('/') ? new URL(`${url.href}Verify.cs`) : url), d.path)
  }
  assert.equal(OPENAPI_PATH, '/api/public/v1/openapi.json')
})
