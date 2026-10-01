// REOS-82 unit tests: the external-link URL is built only from the configured https base URL and a strictly shaped key (SEC-C), and the four sync words.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { brokenReason, externalUrl, freshness } from '../src/externalLinks.ts'

test('Jira issue keys link to /browse on the configured base URL; fix versions stay text', () => {
  assert.equal(externalUrl('Jira', 'https://acme.atlassian.net', 'PAY-123'), 'https://acme.atlassian.net/browse/PAY-123')
  assert.equal(externalUrl('Jira', 'https://jira.example.com/jira/', 'PAY-123'), 'https://jira.example.com/jira/browse/PAY-123')
  assert.equal(externalUrl('Jira', 'https://acme.atlassian.net', 'PAY/4.5.0'), null)
})

test('ServiceNow changes and change tasks link to their table with the number as a query', () => {
  assert.equal(externalUrl('ServiceNow', 'https://acme.service-now.com', 'CHG0030001'), 'https://acme.service-now.com/change_request.do?sysparm_query=number%3DCHG0030001')
  assert.equal(externalUrl('ServiceNow', 'https://acme.service-now.com', 'CTASK0010001'), 'https://acme.service-now.com/change_task.do?sysparm_query=number%3DCTASK0010001')
})

test('no URL without a safe base URL or with a key of any other shape', () => {
  for (const base of [null, undefined, '', 'not a url', 'http://acme.atlassian.net', 'javascript:alert(1)', 'data:text/html,x', 'https://u:p@acme.atlassian.net', 'https://acme.atlassian.net/?x=1', 'https://acme.atlassian.net/#x'])
    assert.equal(externalUrl('Jira', base, 'PAY-1'), null, String(base))
  for (const key of ['pay-1', 'PAY-1/../../x', 'PAY-1"><script>', 'javascript:alert(1)', 'PAY-1?x=1', ' PAY-1', 'INC0001'])
    assert.equal(externalUrl('Jira', 'https://acme.atlassian.net', key), null, key)
  assert.equal(externalUrl('ServiceNow', 'https://acme.service-now.com', 'CHG1'), null)
  assert.equal(externalUrl('Other', 'https://acme.service-now.com', 'CHG0030001'), null)
  // a base URL's own path is kept but the key can never leave it
  assert.ok(externalUrl('Jira', 'https://evil.example/', 'PAY-1')!.startsWith('https://evil.example/browse/'))
})

test('sync state words: broken, not yet checked, stale, fresh', () => {
  assert.deepEqual(freshness({ syncState: 'NotFound', lastSyncedAt: '2026-10-01T00:00:00Z', stale: false }).word, 'broken')
  assert.deepEqual(freshness({ syncState: 'AuthFailed', lastSyncedAt: null, stale: true }).word, 'broken')
  assert.equal(freshness({ syncState: 'Unsynced', lastSyncedAt: null, stale: false }).word, 'not yet checked')
  assert.equal(freshness({ syncState: 'InSync', lastSyncedAt: '2026-10-01T00:00:00Z', stale: true }).word, 'stale')
  assert.equal(freshness({ syncState: 'InSync', lastSyncedAt: '2026-10-01T00:00:00Z', stale: false }).word, 'fresh')
  assert.equal(freshness({ syncState: 'Mismatch', lastSyncedAt: '2026-10-01T00:00:00Z', stale: false }).word, 'fresh')
  assert.equal(brokenReason('NotFound'), 'not found in the source system')
})
