// Verifies a ReleaseOS webhook delivery (docs/WEBHOOKS.md). Node 18+:  node docs/samples/webhooks/verify.mjs --selftest
// In a real receiver, call verify() with the RAW request body (a Buffer or string exactly as received, not JSON.stringify of a parsed object) and the three headers.
import { createHmac, timingSafeEqual } from 'node:crypto'
import { readFileSync } from 'node:fs'

export const TOLERANCE_SECONDS = 300

/** secret: the whsec_ secret shown once. timestamp, deliveryId, signature: the X-ReleaseOS-Timestamp, -Delivery and -Signature headers. now: your clock in Unix seconds. */
export function verify(secret, timestamp, deliveryId, rawBody, signature, now = Math.floor(Date.now() / 1000)) {
  const ts = Number(timestamp)
  if (!signature || !Number.isInteger(ts) || Math.abs(now - ts) > TOLERANCE_SECONDS) return false   // replay window
  const mac = createHmac('sha256', secret).update(`${ts}.${deliveryId}.`).update(rawBody).digest('hex')
  const expected = Buffer.from(`v1=${mac}`)
  const given = Buffer.from(signature)
  return expected.length === given.length && timingSafeEqual(expected, given)   // constant time
}
// After verify(): remember deliveryId (for a day or more) and answer 200 without acting when you have seen it: delivery is at least once.

if (process.argv.includes('--check')) {
  // --check <file.json>: a captured delivery {secret, timestamp, deliveryId, signature, body, now}; exit 0 and print ok when it verifies (docs/DEVELOPERS.md).
  const c = JSON.parse(readFileSync(process.argv[process.argv.indexOf('--check') + 1], 'utf8'))
  const ok = verify(c.secret, c.timestamp, c.deliveryId, Buffer.from(c.body, 'utf8'), c.signature, c.now)
  console.log(ok ? 'ok' : 'FAILED')
  process.exit(ok ? 0 : 1)
}

if (process.argv.includes('--selftest')) {
  const secret = 'whsec_test_secret', delivery = '7d1c0a52-8d3e-4f5e-9d3b-0c1b6f6e2a11', body = '{"id":"evt_1","type":"gate.failed"}', ts = 1792504800
  const sig = 'v1=ac276cd9bebb669b711a37e14b70c1398c4d7b8f6a6ed3f563b8813fb4f43dff'
  const ok = verify(secret, String(ts), delivery, body, sig, ts)
    && !verify(secret, String(ts), delivery, body + ' ', sig, ts)        // body changed
    && !verify('whsec_other', String(ts), delivery, body, sig, ts)       // wrong secret
    && !verify(secret, String(ts), delivery, body, sig, ts + 301)        // outside the replay window
    && verify(secret, String(ts), delivery, body, sig, ts + 300)         // inside it
  console.log(ok ? 'ok' : 'FAILED')
  process.exit(ok ? 0 : 1)
}
