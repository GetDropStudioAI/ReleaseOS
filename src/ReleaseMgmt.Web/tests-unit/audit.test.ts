// REOS-101: the Audit history screen's pure parts: the filter query, the derived outcome, event ids, the not-colour-alone diff, the copied record and the URL.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { EMPTY_FILTERS, anyFilter, auditPath, auditQuery, diffLines, eventFromSearch, eventId, eventRecord, eventTitle, outcomeOf, type AuditItem } from '../src/auditLogic.ts'

const ITEM: AuditItem = {
  id: 87924, occurredAt: '2026-10-23T14:22:00Z', actorUserId: 'u1', actorName: 'Maya Chen', releaseTrainId: 't1', trainTitle: 'R26.12', entityType: 'StageGate', entityId: 'g1', action: 'Certify',
  beforeJson: '{"status":"InProgress","version":3}', afterJson: '{"status":"Certified","version":4}',
}

test('every filter combines into one query, and a search is the event-name filter', () => {
  assert.equal(auditQuery(EMPTY_FILTERS).toString(), '')
  const q = auditQuery({ train: 't1', entity: 'StageGate', actor: 'u1', search: '  cert ', from: '2026-10-01', to: '2026-10-23' })
  assert.equal(q.toString(), 'train=t1&entity=StageGate&actor=u1&action=cert&from=2026-10-01&to=2026-10-23')   // dates are UTC days (D44), sent as they are
  assert.equal(anyFilter(EMPTY_FILTERS), false)
  assert.equal(anyFilter({ ...EMPTY_FILTERS, from: '2026-10-01' }), true)
})

test('event ids, titles and outcomes', () => {
  assert.equal(eventId(87924), 'EVT-087924')
  assert.equal(eventId(7), 'EVT-000007')
  assert.equal(eventTitle('StageGate', 'Certify'), 'Certify stage gate')
  assert.equal(eventTitle('GoNoGoCondition', 'Close'), 'Close go no go condition')
  for (const [action, outcome] of [['Fail', 'Failed'], ['Abort', 'Failed'], ['Decertify', 'Failed'], ['Request', 'Pending'], ['RequestOverride', 'Pending'], ['Approve', 'Approved'],
    ['Certify', 'Approved'], ['Grant', 'Approved'], ['Record', 'Recorded'], ['Create', 'Recorded'], ['Add', 'Recorded']]) assert.equal(outcomeOf(action), outcome, action)
})

test('the diff has a removed and an added line for a changed key, and a plain line for an unchanged one; each is marked without colour', () => {
  const lines = diffLines({ status: 'InProgress', version: 3, owner: 'a' }, { status: 'Certified', version: 4, owner: 'a' })
  assert.deepEqual(lines.map(l => `${l.kind}:${l.text}`), ['removed:status: "InProgress"', 'added:status: "Certified"', 'removed:version: 3', 'added:version: 4', 'same:owner: "a"'])
  assert.deepEqual(diffLines(undefined, { a: 1 }).map(l => l.kind), ['added'])                 // a create has only an after
  assert.deepEqual(diffLines({ a: 1 }, undefined).map(l => l.kind), ['removed'])               // a delete has only a before
  assert.deepEqual(diffLines({ a: 1 }, { b: 2 }).map(l => `${l.kind}:${l.key}`), ['removed:a', 'added:b'])
  assert.deepEqual(diffLines('text before', 'text after').map(l => l.kind), ['removed', 'added'])
  assert.deepEqual(diffLines(undefined, undefined), [])
})

test('the copied record is complete and machine-readable: every column, the sides parsed, the outcome derived', () => {
  const r = eventRecord(ITEM)
  assert.deepEqual(r, {
    id: 87924, eventId: 'EVT-087924', occurredAt: '2026-10-23T14:22:00Z', actor: { userId: 'u1', name: 'Maya Chen' }, release: { id: 't1', title: 'R26.12' },
    entity: { type: 'StageGate', id: 'g1' }, action: 'Certify', outcome: 'Approved', before: { status: 'InProgress', version: 3 }, after: { status: 'Certified', version: 4 },
  })
  assert.deepEqual(JSON.parse(JSON.stringify(r)), r)                                           // survives a round trip through the clipboard
  assert.equal(eventRecord({ ...ITEM, beforeJson: null, afterJson: null }).before, null)
})

test('the open event is in the URL, and only a plain number counts', () => {
  assert.equal(auditPath(87924), '/audit?event=87924')
  assert.equal(auditPath(null), '/audit')
  assert.equal(eventFromSearch('?event=87924'), 87924)
  assert.equal(eventFromSearch('?x=1'), null)
  assert.equal(eventFromSearch('?event=abc'), null)
  assert.equal(eventFromSearch('?event=-5'), null)
})
