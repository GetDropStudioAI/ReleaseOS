// REOS-118 (RT3): clients — how a segment reads, what the form asks for, and what a save sends.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { clientProblem, environmentOptions, saveBody, segmentLabel, sortClients, summary, type ClientRow, type EnvironmentChoice } from '../src/mgmt/clientsLogic.ts'

const cl = (id: string, name: string, over: Partial<ClientRow> = {}): ClientRow =>
  ({ id, name, segment: 'Standard', certEnvironment: null, archived: false, version: 1, ...over })
const acme = cl('a', 'Acme')
const globex = cl('g', 'Globex', { segment: 'OnDemand', certEnvironment: { id: 'e1', name: 'CERT' } })
const old = cl('o', 'Aardvark', { archived: true })

test('the stored segment reads as a person writes it', () => {
  assert.equal(segmentLabel('OnDemand'), 'On-Demand')
  assert.equal(segmentLabel('Standard'), 'Standard')
})

test('clients sort by name, archived last', () => {
  assert.deepEqual(sortClients([globex, old, acme]).map(c => c.name), ['Acme', 'Globex', 'Aardvark'])
  assert.deepEqual(sortClients([cl('x', 'beta'), cl('y', 'Alpha')]).map(c => c.name), ['Alpha', 'beta'])
})

test('Add and Save ask for a name, and an On-Demand client needs its CERT environment', () => {
  assert.equal(clientProblem('', 'Standard', ''), 'Enter a name for the client.')
  assert.equal(clientProblem('   ', 'OnDemand', 'e1'), 'Enter a name for the client.')
  assert.equal(clientProblem('Acme', 'Standard', ''), null)
  assert.equal(clientProblem('Acme', 'Standard', 'e1'), null, 'a Standard client ignores a leftover environment: the save does not send it')
  assert.equal(clientProblem('Globex', 'OnDemand', ''), 'Choose the CERT environment of this On-Demand client.')
  assert.equal(clientProblem('Globex', 'OnDemand', 'e1'), null)
})

test('a save sends the environment for an On-Demand client only; a Standard client sends none, which clears it', () => {
  assert.deepEqual(saveBody(' Globex ', 'OnDemand', 'e1'), { name: 'Globex', segment: 'OnDemand', certEnvironmentId: 'e1' })
  assert.deepEqual(saveBody('Acme', 'Standard', 'e1'), { name: 'Acme', segment: 'Standard', certEnvironmentId: '' })
})

test('the CERT picker offers active environments, and keeps an archived one the client already has, with its state in words', () => {
  const envs: EnvironmentChoice[] = [{ id: 'e1', name: 'CERT', archived: false }, { id: 'e2', name: 'Old CERT', archived: true }, { id: 'e3', name: 'Other', archived: true }]
  assert.deepEqual(environmentOptions(envs, null).map(o => o.label), ['Choose an environment', 'CERT'])
  assert.deepEqual(environmentOptions(envs, 'e2').map(o => o.label), ['Choose an environment', 'CERT', 'Old CERT (archived)'])
  assert.equal(environmentOptions(envs, 'e2')[0].value, '')
})

test('the summary counts active clients by segment and leaves archived ones out', () => {
  assert.deepEqual(summary([acme, globex, old, cl('b', 'Bravo', { segment: 'OnDemand', certEnvironment: { id: 'e1', name: 'CERT' } })]), { active: 3, standard: 1, onDemand: 2 })
  assert.deepEqual(summary([]), { active: 0, standard: 0, onDemand: 0 })
})
