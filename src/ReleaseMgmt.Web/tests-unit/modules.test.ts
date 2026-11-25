// REOS-174 (D56, D57, D63): add-on modules. Which entries the navigation shows, the words of the settings page and its dialogs, and the rule that a module is never a toggle.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { NAV, jumpTargets, moduleOfView, navFor } from '../src/shell/nav.ts'
import { parsePath, isKnownPath } from '../src/route.ts'
import { DATA_KEPT, disableIntro, disableLabel, enableBlocker, enableIntro, enableLabel, enabledKeys, planLabel, stateLabel, summary, type ModuleRow } from '../src/mgmt/modulesLogic.ts'

const cab = (over: Partial<ModuleRow> = {}): ModuleRow => ({
  key: 'cab', name: 'CAB module', summary: 'x', turnsOn: ['a', 'b'], enabled: false, entitled: true, entitlementReason: null, planSource: 'Default',
  enabledBy: null, enabledAt: null, lastChangedAt: null, version: 0, ...over,
})
const views = (roles: string[], modules: Set<string> | null) => navFor(roles, modules).flatMap(g => g.items.map(i => i.view))

test('with every module off, or not known yet, no module entry is in the navigation or in Jump to', () => {
  for (const modules of [null, new Set<string>()]) {
    const v = views(['RTE'], modules)
    assert.ok(!v.includes('cab') && !v.includes('ai'))
    const names = jumpTargets(['RTE'], modules).map(t => t.name)
    assert.ok(!names.includes('Change advisory board') && !names.includes('AI assistant'))
  }
  assert.deepEqual(views(['RTE'], null), views(['RTE'], new Set()))   // the core navigation is the same whether or not the modules are known
})

test('an enabled module adds its own entry and nobody else’s', () => {
  assert.ok(views(['Viewer'], new Set(['cab'])).includes('cab') && !views(['Viewer'], new Set(['cab'])).includes('ai'))
  assert.ok(views(['Viewer'], new Set(['ai'])).includes('ai') && !views(['Viewer'], new Set(['ai'])).includes('cab'))
  assert.ok(jumpTargets(['Viewer'], new Set(['cab'])).some(t => t.view === 'cab'))
  const core = views(['RTE'], new Set())
  const both = views(['RTE'], new Set(['cab', 'ai']))
  assert.deepEqual(both.filter(v => v !== 'cab' && v !== 'ai'), core)   // nothing else moves
})

test('a page of a module knows its module; core pages belong to none', () => {
  assert.equal(moduleOfView('cab'), 'cab')
  assert.equal(moduleOfView('ai'), 'ai')
  assert.equal(moduleOfView('approvals'), undefined)
  assert.equal(moduleOfView('admin'), undefined)
  assert.ok(NAV.flatMap(g => g.items).filter(i => i.module).every(i => i.view === i.module))
  assert.equal(parsePath('/cab').view, 'cab')
  assert.ok(isKnownPath('/ai'))
})

test('the enabled keys are the rows that are on', () => {
  assert.deepEqual([...enabledKeys([cab({ enabled: true }), cab({ key: 'ai', enabled: false })])], ['cab'])
  assert.equal(enabledKeys([]).size, 0)
})

test('the page and its dialogs use a button, never a switch: the labels are words and say what turns on and that data is kept', () => {
  const r = cab()
  assert.equal(enableLabel(r), 'Enable CAB module')   // D63
  assert.equal(disableLabel(r), 'Disable CAB module')
  assert.equal(stateLabel(r), 'Not enabled')
  assert.equal(stateLabel(cab({ enabled: true })), 'Enabled')
  assert.match(enableIntro(r), /turns on/)
  assert.match(disableIntro(r), /turns these off/)
  assert.match(DATA_KEPT, /data is kept/)
  assert.equal(summary([cab({ enabled: true }), cab({ key: 'ai' })]), '1 of 2 enabled')
})

test('a plan that does not include the module says so in words, with the plan’s reason when it gave one', () => {
  assert.equal(enableBlocker(cab()), null)
  assert.equal(enableBlocker(cab({ entitled: false })), 'Your plan does not include the CAB module.')
  assert.equal(enableBlocker(cab({ entitled: false, entitlementReason: 'Upgrade to Team.' })), 'Upgrade to Team.')
  assert.equal(planLabel(cab({ entitled: false })), 'Not in your plan')
  assert.equal(planLabel(cab({ planSource: 'Plan' })), 'Included in your plan')
  assert.equal(planLabel(cab()), 'Available')
})
