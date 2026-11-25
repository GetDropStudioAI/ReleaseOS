// REOS-155 (B8): service catalog — search and filter, what the form asks for, and what a save sends.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { archiveImpact, createBody, draftOf, emptyDraft, filterServices, isDirty, patchBody, serviceProblem, sortServices, summary, teamOptions, type ServiceRow } from '../src/catalog/catalogLogic.ts'

const svc = (key: string, name: string, over: Partial<ServiceRow> = {}): ServiceRow =>
  ({ id: key, serviceKey: key, name, ownerTeam: null, repository: null, projectCode: null, archived: false, products: 0, deployments: 0, version: 1, ...over })
const api = svc('payments-api', 'Payments API', { ownerTeam: { id: 't1', handle: 'pay', name: 'Payments' }, repository: 'git.example/pay/api', projectCode: 'PAY' })
const web = svc('web', 'Web shop', { products: 2, deployments: 1 })
const old = svc('aaa-legacy', 'Legacy', { archived: true })

test('services sort by key, archived last', () => {
  assert.deepEqual(sortServices([old, web, api]).map(s => s.serviceKey), ['payments-api', 'web', 'aaa-legacy'])
})

test('search matches key, name, team, repository and project code, any case, every word', () => {
  const all = [api, web, old]
  assert.deepEqual(filterServices(all, 'PAYMENTS', 'all').map(s => s.id), ['payments-api'])
  assert.deepEqual(filterServices(all, 'shop', 'all').map(s => s.id), ['web'])
  assert.deepEqual(filterServices(all, '@pay', 'all').map(s => s.id), ['payments-api'], 'the team handle')
  assert.deepEqual(filterServices(all, 'git.example pay', 'all').map(s => s.id), ['payments-api'])
  assert.deepEqual(filterServices(all, 'payments shop', 'all'), [], 'every word has to match')
  assert.equal(filterServices(all, '  ', 'all').length, 3)
})

test('the state filter keeps active, archived or all', () => {
  const all = [api, web, old]
  assert.deepEqual(filterServices(all, '', 'active').map(s => s.id), ['payments-api', 'web'])
  assert.deepEqual(filterServices(all, '', 'archived').map(s => s.id), ['aaa-legacy'])
  assert.equal(filterServices(all, '', 'all').length, 3)
  assert.deepEqual(summary(all), { active: 2, archived: 1 })
})

test('Add asks for a valid key and a name; Save only for a name; each reason is a sentence', () => {
  const d = { ...emptyDraft() }
  assert.equal(serviceProblem(d, true), 'Enter a key for the service.')
  assert.match(serviceProblem({ ...d, key: 'has space', name: 'X' }, true)!, /letters, digits/)
  assert.equal(serviceProblem({ ...d, key: 'ok.key_1-a' }, true), 'Enter a name for the service.')
  assert.equal(serviceProblem({ ...d, key: 'ok', name: 'Name' }, true), null)
  assert.equal(serviceProblem({ ...d, key: '', name: 'Name' }, false), null, 'the key is not asked for when editing')
  assert.match(serviceProblem({ ...d, name: 'x'.repeat(201) }, false)!, /at most 200/)
  assert.match(serviceProblem({ ...d, name: 'n', repository: 'r'.repeat(301) }, false)!, /repository/)
  assert.match(serviceProblem({ ...d, name: 'n', projectCode: 'p'.repeat(101) }, false)!, /project code/)
  assert.match(serviceProblem({ ...d, key: 'k'.repeat(61), name: 'n' }, true)!, /1 to 60/)
})

test('dirty compares trimmed values with the saved row', () => {
  assert.equal(isDirty(draftOf(api), api), false)
  assert.equal(isDirty({ ...draftOf(api), name: ' Payments API ' }, api), false)
  assert.equal(isDirty({ ...draftOf(api), name: 'Payments v2' }, api), true)
  assert.equal(isDirty({ ...draftOf(api), ownerTeamId: '' }, api), true)
  assert.equal(isDirty({ ...draftOf(web), repository: 'x' }, web), true)
})

test('a save clears with empty strings and never sends the key; a create leaves empty fields out', () => {
  assert.deepEqual(patchBody({ key: 'k', name: ' N ', ownerTeamId: '', repository: ' ', projectCode: 'P' }), { name: 'N', ownerTeamId: '', repository: '', projectCode: 'P' })
  assert.deepEqual(createBody({ key: ' svc ', name: 'N', ownerTeamId: '', repository: '', projectCode: '' }), { serviceKey: 'svc', name: 'N', ownerTeamId: null, repository: null, projectCode: null })
  assert.deepEqual(createBody({ key: 'svc', name: 'N', ownerTeamId: 't1', repository: 'r', projectCode: 'p' }), { serviceKey: 'svc', name: 'N', ownerTeamId: 't1', repository: 'r', projectCode: 'p' })
})

test('the team picker starts with no team, is sorted, and keeps a current owner missing from the list', () => {
  const teams = [{ id: 'b', handle: 'beta', name: 'Beta' }, { id: 'a', handle: 'alpha', name: 'Alpha' }]
  assert.deepEqual(teamOptions(teams, null).map(o => o.label), ['No owner team', 'Alpha (@alpha)', 'Beta (@beta)'])
  assert.deepEqual(teamOptions(teams, { id: 'z', handle: 'zed', name: 'Zed' }).map(o => o.value), ['', 'a', 'b', 'z'])
})

test('archiving says what keeps using the service', () => {
  assert.equal(archiveImpact({ products: 0, deployments: 0 }), 'Nothing uses it yet.')
  assert.match(archiveImpact({ products: 1, deployments: 0 }), /the 1 product that use it/)
  assert.match(archiveImpact({ products: 2, deployments: 3 }), /2 products and 3 deployments/)
})
