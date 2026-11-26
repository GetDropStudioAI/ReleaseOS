// REOS-162: API tokens screen — access presets, expiry choices from a clock, state tone, why Make is off, sort and the words of the confirmations.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { chipTone } from '../src/ui/tone.ts'
import {
  ACCESS_PRESETS, EXPIRY_OPTIONS, KNOWN_SCOPES, REVOKED_REASON, defaultRotateExpiry, expiryIso, footerText, makeBody, makeProblem, presetOfScopes, presetsFor, presetsOutside,
  revokeImpact, rotateBody, rotateImpact, rotateOptionsFor, scopesOfPreset, scopesText, sortTokens, stateTone, type ApiToken,
} from '../src/tokens/tokensLogic.ts'

const tok = (id: string, state: ApiToken['state'], createdAt: string, over: Partial<ApiToken> = {}): ApiToken => ({
  id, name: `Token ${id}`, tokenPrefix: `rt_${id}`, scopes: ['read'], expiresAt: null, state, ownerUserId: 'u1', ownerName: 'Ada', createdAt, rotatedAt: null, lastUsedAt: null,
  revokedAt: state === 'Revoked' ? createdAt : null, revokedBy: null, version: 1, ...over,
})

test('the presets are the seven the design lists, each option says its scopes in words, and they round trip', () => {
  assert.deepEqual(ACCESS_PRESETS.map(p => p.scopes), [
    ['read'], ['read', 'write'], ['read', 'approvals'], ['read', 'deployments'], ['read', 'write', 'approvals'], ['read', 'write', 'deployments'], ['read', 'write', 'approvals', 'deployments'],
  ])
  assert.equal(ACCESS_PRESETS[0].label, 'Read (read)')
  assert.equal(ACCESS_PRESETS[6].label, 'Everything (read, write, approvals, deployments)')
  for (const p of ACCESS_PRESETS) {
    assert.deepEqual(scopesOfPreset(p.id), p.scopes)
    assert.equal(presetOfScopes(p.scopes), p.id)
    assert.equal(presetOfScopes([...p.scopes].reverse()), p.id, 'any order')
    for (const s of p.scopes) assert.ok(p.label.includes(s), `${p.id} names ${s}`)
  }
  assert.equal(presetOfScopes(['write']), null)
  assert.deepEqual(scopesOfPreset('nope'), [], 'an unknown preset asks for nothing')
  scopesOfPreset('read').push('write')
  assert.deepEqual(scopesOfPreset('read'), ['read'], 'a copy is returned')
})

test('no preset ever produces a scope the server does not know', () => {
  assert.deepEqual(presetsOutside(KNOWN_SCOPES), [])
  for (const p of ACCESS_PRESETS) for (const s of scopesOfPreset(p.id)) assert.ok((KNOWN_SCOPES as readonly string[]).includes(s), s)
  // a server that knows fewer scopes: only the presets it can honour are offered
  assert.deepEqual(presetsFor(['read', 'write']).map(p => p.id), ['read', 'read-write'])
  assert.deepEqual(presetsFor(KNOWN_SCOPES).length, 7)
  assert.deepEqual(presetsFor([]), [])
  assert.deepEqual(presetsOutside(['read']).length, 6)
})

test('expiry choices become UTC instants from the clock; never and keep send nothing', () => {
  const now = new Date('2026-11-25T10:30:00.000Z')
  assert.equal(expiryIso('never', now), null)
  assert.equal(expiryIso('keep', now), null)
  assert.equal(expiryIso('30d', now), '2026-12-25T10:30:00.000Z')
  assert.equal(expiryIso('90d', now), '2027-02-23T10:30:00.000Z')
  assert.equal(expiryIso('1y', now), '2027-11-25T10:30:00.000Z')
  assert.equal(expiryIso('1y', new Date('2028-02-29T00:00:00.000Z')), '2029-03-01T00:00:00.000Z', 'a leap day rolls forward, still in the future')
  assert.equal(now.toISOString(), '2026-11-25T10:30:00.000Z', 'the clock is not changed')
  assert.deepEqual(EXPIRY_OPTIONS.map(o => o.label), ['Never', 'In 30 days', 'In 90 days', 'In 1 year'])
  for (const o of EXPIRY_OPTIONS) { const at = expiryIso(o.value, now); if (at) assert.ok(Date.parse(at) > now.getTime() && at.endsWith('Z')) }
})

test('the bodies: make trims the name and sends the scopes and expiry; rotate sends {} unless a new expiry was chosen', () => {
  const now = new Date('2026-11-25T10:30:00.000Z')
  assert.deepEqual(makeBody('  Build server ', 'read-write', '30d', now), { name: 'Build server', scopes: ['read', 'write'], expiresAt: '2026-12-25T10:30:00.000Z' })
  assert.deepEqual(makeBody('x', 'read', 'never', now), { name: 'x', scopes: ['read'], expiresAt: null })
  assert.deepEqual(rotateBody('keep', now), {})
  assert.deepEqual(rotateBody('90d', now), { expiresAt: '2027-02-23T10:30:00.000Z' })
})

test('an expired token has to be given a new expiry to rotate; a live one may keep its own', () => {
  assert.deepEqual(rotateOptionsFor({ state: 'Active' }).map(o => o.value), ['keep', '30d', '90d', '1y'])
  assert.deepEqual(rotateOptionsFor({ state: 'Expired' }).map(o => o.value), ['30d', '90d', '1y'])
  assert.equal(defaultRotateExpiry({ state: 'Active' }), 'keep')
  assert.notEqual(defaultRotateExpiry({ state: 'Expired' }), 'keep')
  assert.ok(rotateOptionsFor({ state: 'Expired' }).some(o => o.value === defaultRotateExpiry({ state: 'Expired' })))
})

test('state tone: Active success, Expired warning, Revoked neutral, and chipTone agrees', () => {
  assert.deepEqual((['Active', 'Expired', 'Revoked'] as const).map(stateTone), ['success', 'warning', 'neutral'])
  for (const s of ['Active', 'Expired', 'Revoked'] as const) assert.equal(chipTone(s), stateTone(s), s)
})

test('Make stays off with its reason in words until the name is filled', () => {
  assert.equal(makeProblem('', 'read'), 'Enter a name for the token.')
  assert.equal(makeProblem('   ', 'read'), 'Enter a name for the token.')
  assert.match(makeProblem('x'.repeat(101), 'read')!, /at most 100/)
  assert.equal(makeProblem('x'.repeat(100), 'read'), null)
  assert.equal(makeProblem('Build', 'read'), null)
  assert.equal(makeProblem('Build', ''), 'Choose what the token may do.')
})

test('revoked tokens sort last, the rest newest first; the footer counts', () => {
  const rows = [tok('a', 'Revoked', '2026-11-03T00:00:00Z'), tok('b', 'Active', '2026-11-01T00:00:00Z'), tok('c', 'Expired', '2026-11-02T00:00:00Z'), tok('d', 'Revoked', '2026-11-04T00:00:00Z')]
  assert.deepEqual(sortTokens(rows).map(t => t.id), ['c', 'b', 'd', 'a'])
  assert.deepEqual(rows.map(t => t.id), ['a', 'b', 'c', 'd'], 'the input is not changed')
  assert.equal(footerText(rows), '4 tokens · 1 active')
  assert.equal(footerText([rows[1]]), '1 token · 1 active')
  assert.equal(scopesText(['read', 'write']), 'read, write')
  assert.equal(scopesText([]), 'none')
})

test('the confirmations say what stops working at once, and the revoked reason is a sentence', () => {
  assert.match(revokeImpact({ name: 'Build' }), /Build stops working at once/)
  assert.match(rotateImpact({ name: 'Build' }, 'Grace'), /old secret of Build stops working at once.*acts as Grace/)
  assert.equal(REVOKED_REASON, 'This token is revoked; make a new one.')
})
