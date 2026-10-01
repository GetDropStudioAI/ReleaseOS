// REOS-65: the sign-in page turns the server's reason code into words, and never shows what the URL carries as-is.
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import { signInMessage, withoutSignInReason } from '../src/signinReason.ts'

test('no code, no message', () => {
  assert.equal(signInMessage(''), null)
  assert.equal(signInMessage('?tab=trains'), null)
})

test('each server reason has its own words', () => {
  assert.match(signInMessage('?signin=no-role')!, /no role in this app/)
  assert.match(signInMessage('?signin=email-unverified')!, /not verified/)
  assert.match(signInMessage('?signin=no-email')!, /did not send an email address/)
  assert.match(signInMessage('?signin=denied')!, /cancelled or refused/)
  assert.match(signInMessage('?signin=expired')!, /Sign in again/)
  assert.match(signInMessage('?signin=failed')!, /did not complete/)
})

test('an unknown or crafted code shows the generic message, never its own text', () => {
  const crafted = signInMessage('?signin=' + encodeURIComponent('<b>Call +1 555 0100 to restore access</b>'))!
  assert.equal(crafted, signInMessage('?signin=failed'))
  assert.equal(signInMessage('?signin=constructor'), signInMessage('?signin=failed'))   // no prototype keys
})

test('the code is removed from the address so a reload does not repeat it', () => {
  assert.equal(withoutSignInReason('?signin=no-role'), '')
  assert.equal(withoutSignInReason('?signin=no-role&x=1'), '?x=1')
})

test('every reason code the server sends has words here', () => {
  const cs = readFileSync(new URL('../../ReleaseMgmt.Api/Auth/OidcSignIn.cs', import.meta.url), 'utf8')
  const block = cs.slice(cs.indexOf('public static class Reasons'), cs.indexOf('public const string QueryKey'))
  const codes = [...block.matchAll(/=\s*"([a-z-]+)"/g)].map(m => m[1])
  assert.ok(codes.length >= 6, 'found the server reason codes')
  for (const c of codes) if (c !== 'failed') assert.notEqual(signInMessage(`?signin=${c}`), signInMessage('?signin=failed'), `no words for ${c}`)
  assert.match(cs, /QueryKey = "signin"/)
})
