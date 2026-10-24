// REOS-136 (D44): UTC with the person's own time beside it. Every case names its zone, so nothing here depends on the machine's zone.
import test from 'node:test'
import assert from 'node:assert/strict'
import { inputZoneNote, rangeParts, rangeText, timeParts, timeText, utcDayLabel, utcHM } from '../src/utcTime.ts'

const at = Date.UTC(2026, 9, 30, 14, 32, 45)   // Fri 30 Oct 2026 14:32:45 UTC

test('a time is UTC and labelled, with the local time and zone beside it', () => {
  assert.equal(utcHM(at), '14:32')
  assert.equal(utcDayLabel(at), 'Fri 30 Oct')
  assert.equal(timeText(at, { zone: 'America/Chicago' }), '14:32 UTC (09:32 CDT)')
  assert.equal(timeText(at, { zone: 'America/Chicago', date: true }), 'Fri 30 Oct 14:32 UTC (09:32 CDT)')
  assert.equal(timeText('2026-10-30T14:32:45Z', { zone: 'Europe/Berlin' }), '14:32 UTC (15:32 GMT+1)')
})

test('a person who works in UTC sees one time, not two', () => {
  const p = timeParts(at, { zone: 'UTC', date: true })
  assert.equal(p.local, null)
  assert.equal(p.text, 'Fri 30 Oct 14:32 UTC')
  assert.equal(timeParts(at, { zone: 'Etc/UTC' }).local, null)
})

test('the local part says so when it is another day than UTC\'s', () => {
  const late = Date.UTC(2026, 9, 30, 2, 0)
  assert.equal(timeText(late, { zone: 'America/Chicago' }), '02:00 UTC (29 OCT 21:00 CDT)')
})

test('the screen-reader label spells out the zone', () => {
  assert.match(timeParts(at, { zone: 'America/Chicago' }).label, /^30 October 2026 at 14:32 UTC; your time 09:32 Central Daylight Time$/)
  assert.equal(timeParts(at, { zone: 'UTC' }).label, '30 October 2026 at 14:32 UTC')
})

test('a window is one UTC range, with a second date when it ends on another UTC day', () => {
  const a = Date.UTC(2026, 10, 6, 6, 0), b = Date.UTC(2026, 10, 6, 10, 0), c = Date.UTC(2026, 10, 7, 2, 0)
  assert.equal(rangeText(a, b, { zone: 'UTC' }), 'Fri 6 Nov 06:00–10:00 UTC')
  assert.equal(rangeText(a, b, { zone: 'America/Chicago' }), 'Fri 6 Nov 06:00–10:00 UTC (00:00 CST – 04:00 CST)')
  assert.equal(rangeParts(a, c, { zone: 'UTC' }).utc, 'Fri 6 Nov 06:00 – Sat 7 Nov 02:00 UTC')
})

test('a datetime-local field says which zone it edits in, and what the value is in UTC', () => {
  assert.equal(inputZoneNote('America/Chicago', 'CST'), 'Entered in CST (America/Chicago), not UTC')
  assert.equal(inputZoneNote('America/Chicago', 'CST', '2026-12-02T02:00:00Z'), 'Entered in CST (America/Chicago), not UTC · = Wed 2 Dec 02:00 UTC')
  assert.equal(inputZoneNote('UTC', 'UTC', '2026-12-02T02:00:00Z'), 'Entered in UTC')
})
