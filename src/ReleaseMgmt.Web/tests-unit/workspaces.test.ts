// REOS-106: the workspace choice (per person, remembered, named on every /api/ call) and the pure parts of the Workspace tab.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { slugFromName, slugProblem } from '../src/mgmt/membersLogic.ts'
import { choose, currentWorkspace, hubQuery, installWorkspaceHeader, pathAfterSwitch, pick, remembered, type WorkspaceChoice } from '../src/workspace.ts'

const ws = (id: string, slug: string, name = slug): WorkspaceChoice => ({ id, slug, name, region: null, role: 'RTE' })
const memory = () => { const m = new Map<string, string>(); return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v) }, m } }

test('which workspace opens: the one chosen last time, else the one the server picked, else the default one, else the first', () => {
  const list = [ws('a', 'alpha'), ws('default', 'default'), ws('b', 'beta')]
  assert.equal(pick(list, 'beta', 'a')?.id, 'b')
  assert.equal(pick(list, 'BETA', null)?.id, 'b')                   // slugs are case-insensitive, as on the server
  assert.equal(pick(list, 'gone', 'a')?.id, 'a')                    // a workspace they have left falls back to what the server resolved
  assert.equal(pick(list, null, 'nope')?.id, 'default')
  assert.equal(pick([ws('a', 'alpha'), ws('b', 'beta')], null, null)?.id, 'a')
  assert.equal(pick([], 'x', 'y'), null)                            // a member of none
})

test('the choice is remembered per person, in storage that may be blocked', () => {
  const s = memory()
  choose('beta', 'user-1', s)
  assert.equal(currentWorkspace(), 'beta')
  assert.equal(remembered('user-1', s), 'beta')
  assert.equal(remembered('user-2', s), null)                       // another person on the same browser has their own
  assert.equal(hubQuery(), '?workspace=beta')
  const blocked = { getItem: () => { throw new Error('blocked') }, setItem: () => { throw new Error('blocked') } }
  assert.doesNotThrow(() => choose('alpha', 'user-1', blocked))     // never throws: the choice then lasts for the page load
  assert.equal(remembered('user-1', blocked), null)
  assert.equal(hubQuery(), '?workspace=alpha')
  choose('we ird/slug', undefined, s)
  assert.equal(hubQuery(), '?workspace=we%20ird%2Fslug')
})

test('switching keeps the page but not a release, which belongs to the workspace it was made in', () => {
  assert.equal(pathAfterSwitch('/approvals'), '/approvals')
  assert.equal(pathAfterSwitch('/admin'), '/admin')
  assert.equal(pathAfterSwitch('/'), '/')
  assert.equal(pathAfterSwitch('/trains/0190-abc'), '/')
  assert.equal(pathAfterSwitch('/trains/0190-abc/gates/g1/tasks/t1'), '/')
  assert.equal(pathAfterSwitch(''), '/')
})

test('every same-origin /api/ call names the workspace, and nothing else is touched', async () => {
  const calls: { url: string; workspace: string | null }[] = []
  const fake = (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url
    calls.push({ url, workspace: new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined)).get('X-Workspace') })
    return Promise.resolve(new Response('{}'))
  }
  ;(globalThis as unknown as { window: unknown }).window = { fetch: fake, location: { href: 'http://app.test/admin' } }
  choose('beta', undefined, null)
  installWorkspaceHeader()
  installWorkspaceHeader()                                          // installed once
  const f = (globalThis as unknown as { window: { fetch: typeof fetch } }).window.fetch
  await f('/api/v1/trains')
  await f('/api/v1/trains', { headers: { 'If-Match': '3' }, method: 'PATCH' })
  await f('/api/v1/trains', { headers: { 'X-Workspace': 'explicit' } })
  await f(new Request('http://app.test/api/v1/me'))
  await f('/healthz')
  await f('https://elsewhere.test/api/v1/trains')
  assert.deepEqual(calls.map(c => c.workspace), ['beta', 'beta', 'explicit', 'beta', null, null])
  assert.equal(calls.length, 6)
})

test('a workspace name suggests a short name the server accepts', () => {
  assert.equal(slugFromName('Payments EMEA'), 'payments-emea')
  assert.equal(slugFromName('  Équipe  Paiements (EU) '), 'equipe-paiements-eu')
  assert.equal(slugFromName('---A--B---'), 'a-b')
  assert.equal(slugFromName('!!!'), '')
  assert.equal(slugFromName('x'.repeat(60)).length, 40)
  assert.equal(slugFromName('a'.repeat(39) + ' b'), 'a'.repeat(39))   // cut at 40, then no trailing hyphen
  for (const n of ['Payments EMEA', 'Équipe Paiements', 'R&D 2027']) assert.equal(slugProblem(slugFromName(n)), null)
})

test('the short name is checked before the request, with the server\'s rule', () => {
  for (const ok of ['a', 'a1', 'payments-emea', '9-lives']) assert.equal(slugProblem(ok), null, ok)
  for (const bad of ['-a', 'a-', 'A', 'a b', 'a_b', 'é', 'a'.repeat(41)]) assert.notEqual(slugProblem(bad), null, bad)
})
