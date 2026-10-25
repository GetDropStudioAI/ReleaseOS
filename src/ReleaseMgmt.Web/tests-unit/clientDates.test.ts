// REOS-119 (RT4): client dates — how a date reads, which advisories show, and what a save sends.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { advisories, againstStandard, dateProblem, datePatch, dateSource, dateText, daysBetween, kindLabel, summaryText, type ClientDateRow, type ClientSchedule } from '../src/release/clientDatesLogic.ts'

const row = (over: Partial<ClientDateRow> = {}): ClientDateRow => ({
  clientId: 'c', name: 'Globex', segment: 'OnDemand', date: '2027-03-12', wave: 'On-Demand', offeredDate: '2027-03-12', chosenDate: null, chosenBy: null, chosenAt: null,
  scheduled: true, archived: false, version: 1, beforeStandardDate: false, freezes: [], ...over,
})
const sched = (rows: ClientDateRow[]): ClientSchedule => ({ trainId: 't', kind: 'Production', typeName: 'Production', standardDate: '2027-03-05', offsetDays: 7, open: true, note: null, rows, unscheduled: 0, warnings: 0 })

test('a calendar date reads with its weekday and no zone', () => {
  assert.equal(dateText('2027-03-05'), 'Fri 5 Mar 2027')
  assert.equal(dateText('2026-12-31'), 'Thu 31 Dec 2026')
})

test('a date is placed against the Standard date in words', () => {
  assert.equal(daysBetween('2027-03-05', '2027-03-12'), 7)
  assert.equal(daysBetween('2027-02-28', '2027-03-01'), 1)
  assert.equal(againstStandard('2027-03-05', '2027-03-05'), 'Standard date')
  assert.equal(againstStandard('2027-03-12', '2027-03-05'), '+7 days')
  assert.equal(againstStandard('2027-03-06', '2027-03-05'), '+1 day')
  assert.equal(againstStandard('2027-03-03', '2027-03-05'), '2 days before')
  assert.equal(againstStandard('2027-03-04', '2027-03-05'), '1 day before')
})

test('the kind reads as people write it', () => {
  assert.equal(kindLabel('Cert'), 'CERT')
  assert.equal(kindLabel('Production'), 'Production')
})

test('where a date comes from is said in words', () => {
  assert.equal(dateSource(row({ segment: 'Standard', offeredDate: null })), 'Standard date')
  assert.equal(dateSource(row({ chosenDate: '2027-03-19' })), 'Chosen by the client')
  assert.equal(dateSource(row()), 'Offered')
  assert.equal(dateSource(row({ scheduled: false })), 'Offered (default)')
})

test('freeze, chill and early dates are advisories in words', () => {
  assert.deepEqual(advisories(row(), '2027-03-05'), [])
  assert.deepEqual(advisories(row({ freezes: [{ id: 'f', name: 'Quarter close', kind: 'Freeze', startsAt: '', endsAt: '' }, { id: 'g', name: 'Spring', kind: 'Chill', startsAt: '', endsAt: '' }] }), '2027-03-05'),
    ['Freeze window "Quarter close" covers this date', 'Chill window "Spring" covers this date'])
  assert.deepEqual(advisories(row({ beforeStandardDate: true }), '2027-03-05'), ['Earlier than the Standard date (Fri 5 Mar 2027)'])
})

test('Save asks for real dates, none before the Standard date', () => {
  assert.equal(dateProblem('', '2027-03-12', '2027-03-05'), null)
  assert.equal(dateProblem('2027-03-05', '', '2027-03-05'), null)
  assert.match(dateProblem('2027-03-04', '', '2027-03-05')!, /chosen date cannot be earlier than the Standard date \(Fri 5 Mar 2027\)/)
  assert.match(dateProblem('', '2027-03-04', '2027-03-05')!, /offered date cannot be earlier/)
  assert.match(dateProblem('2027-02-30', '', '2027-03-05')!, /calendar date/)
  assert.match(dateProblem('', 'soon', '2027-03-05')!, /calendar date/)
})

test('a save sends only what changed; clearing the chosen date sends the empty string', () => {
  const r = row({ chosenDate: '2027-03-19' })
  assert.equal(datePatch(r, '2027-03-19', '2027-03-12'), null)
  assert.deepEqual(datePatch(r, '2027-03-26', '2027-03-12'), { chosenDate: '2027-03-26' })
  assert.deepEqual(datePatch(r, '', '2027-03-12'), { chosenDate: '' })
  assert.deepEqual(datePatch(row(), '', '2027-03-14'), { offeredDate: '2027-03-14' })
  assert.deepEqual(datePatch(row(), '2027-03-20', '2027-03-14'), { chosenDate: '2027-03-20', offeredDate: '2027-03-14' })
  assert.equal(datePatch(row(), '', ''), null)   // an empty offered field leaves the offered date alone
})

test('the summary counts clients by segment', () => {
  const s = sched([row({ segment: 'Standard', name: 'Acme' }), row({ name: 'Globex' }), row({ name: 'Initech' })])
  assert.equal(summaryText(s), '3 clients: 1 Standard, 2 On-Demand')
  assert.equal(summaryText(sched([row()])), '1 client: 1 On-Demand')
})
