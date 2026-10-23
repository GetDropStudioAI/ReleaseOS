// REOS-95: the Overview's pure parts: the range, the chart's scale and wording, the card tones, and the window text.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { RANGES, attentionTone, chartMax, dayText, rangeFrom, shortDay, successTone, windowText, type ActivityRow, type Day } from '../src/shell/overviewApi.ts'

const day = (date: string, successful = 0, withIssues = 0, unsuccessful = 0): Day => ({ date, successful, withIssues, unsuccessful })

test('a range of n days ends today (UTC) and starts n-1 days earlier', () => {
  const now = new Date('2026-10-23T23:30:00Z')
  assert.equal(rangeFrom(14, now), '2026-10-10')
  assert.equal(rangeFrom(1, now), '2026-10-23')
  assert.equal(rangeFrom(7, new Date('2026-03-02T00:10:00Z')), '2026-02-24')   // across a month end
  assert.deepEqual(RANGES.map(r => r.value), ['7', '14', '30', '90'])
})

test('the chart scale is an even number of at least 4 that holds the tallest day', () => {
  assert.equal(chartMax([]), 4)
  assert.equal(chartMax([day('2026-10-01', 1)]), 4)
  assert.equal(chartMax([day('2026-10-01', 3, 1, 1)]), 6)
  assert.equal(chartMax([day('2026-10-01', 9), day('2026-10-02', 4, 1, 1)]), 10)
})

test('each day reads as a sentence, with zero days saying so', () => {
  assert.equal(shortDay('2026-10-05'), '5 Oct')
  assert.equal(dayText(day('2026-10-05')), '5 Oct: no releases completed')
  assert.equal(dayText(day('2026-10-05', 2)), '5 Oct: 2 successful')
  assert.equal(dayText(day('2026-10-05', 2, 1, 3)), '5 Oct: 2 successful, 1 with issues, 3 failed')
})

test('card detail colours: green when success improved, amber when it fell or something needs attention, neutral otherwise', () => {
  assert.equal(successTone(1.2), 'success')
  assert.equal(successTone(-0.5), 'warning')
  assert.equal(successTone(0), 'neutral')
  assert.equal(successTone(null), 'neutral')
  assert.equal(attentionTone(4), 'warning')
  assert.equal(attentionTone(0), 'neutral')
  assert.equal(attentionTone(null), 'neutral')
})

test('a release row shows its booked window in UTC, or its target date while none is booked', () => {
  const row = { targetReleaseDate: '2026-10-28', windowStartsAt: '2026-10-28T02:00:00Z' } as ActivityRow
  assert.match(windowText(row).utc, /^28 Oct 2026, 02:00 UTC$/)
  assert.deepEqual(windowText({ ...row, windowStartsAt: null }), { utc: 'Target 28 Oct 2026', local: null })
})
