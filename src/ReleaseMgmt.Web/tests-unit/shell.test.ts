// REOS-94: the shell's pure parts: the sidebar's destinations in the design's order, the UTC clock with the person's time beside it (D44), and the
// control-plane card's healthy, degraded and down states.
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { NAV, navFor, pageName, pathOf, jumpTargets, placeholderOf } from '../src/shell/nav.ts'
import { clockLabel, localClock, utcClock } from '../src/shell/clock.ts'
import { controlPlane, initials, roleName, type HostHealth } from '../src/shell/health.ts'

test('the Workspace group matches the design in order and labels; the Management items follow', () => {
  assert.deepEqual(NAV[0].items.map(i => i.label), ['Overview', 'Portfolio & calendar', 'Releases', 'Orchestration', 'Approvals', 'Deployments', 'Audit history'])
  assert.deepEqual(NAV[0].items.map(i => i.icon), ['layout-dashboard', 'calendar-days', 'layers', 'workflow', 'shield-check', 'activity', 'history'])
  assert.deepEqual(NAV[1].items.slice(0, 3).map(i => i.label), ['Service catalog', 'Environments', 'Teams & settings'])
})

test('a destination with no screen yet opens a placeholder; every other one opens its screen', () => {
  assert.deepEqual(NAV.flatMap(g => g.items).filter(i => i.placeholder).map(i => i.view), ['orchestration', 'services', 'environments'])
  assert.equal(placeholderOf('calendar'), undefined)
  assert.equal(pathOf('trains'), '/')
  assert.equal(pathOf('approvals'), '/approvals')
})

test('a Viewer sees neither the audit log nor the admin pages; an RTE sees both', () => {
  const views = (roles: string[]) => navFor(roles).flatMap(g => g.items.map(i => i.view))
  assert.ok(!views(['Viewer']).includes('audit') && !views(['Viewer']).includes('admin') && !views(['Viewer']).includes('connectors'))
  assert.ok(views(['RTE']).includes('audit') && views(['RTE']).includes('admin'))
  assert.ok(views(['GovernanceOfficer']).includes('audit') && !views(['GovernanceOfficer']).includes('admin'))
})

test('pages have names for the breadcrumb and title, and Jump to offers what the person can open', () => {
  assert.equal(pageName('trains'), 'Releases')
  assert.equal(pageName('sync'), 'Sync health')
  assert.equal(pageName('inbox'), 'Inbox')
  const names = jumpTargets(['Viewer']).map(t => t.name)
  assert.ok(names.includes('Approvals') && names.includes('Sync health') && names.includes('Inbox') && !names.includes('Audit history'))
})

test('the clock writes UTC as the design does, with the local time and zone beside it', () => {
  const t = Date.UTC(2026, 9, 23, 14, 32, 45)
  assert.equal(utcClock(t), '23 OCT 2026 · 14:32 UTC')
  assert.equal(localClock(t, 'America/Chicago'), '09:32 CDT')
  assert.equal(localClock(t, 'UTC'), null)                                           // working in UTC: one clock
  assert.equal(localClock(Date.UTC(2026, 9, 23, 22, 0), 'Asia/Tokyo'), '24 OCT 07:00 GMT+9')   // a different day says so
  assert.match(clockLabel(t, 'America/Chicago'), /^23 October 2026 at 14:32 UTC; your time 09:32 Central Daylight Time$/)
})

test('the control-plane card: operational, degraded and down, worst first', () => {
  const ok: HostHealth = { status: 'Healthy', db: 'ok', poller: { state: 'running' }, watchdog: { state: 'running' } }
  const sync = { connectorWide: false, openAlerts: 0, watchdog: { stalled: false } }
  assert.deepEqual(controlPlane(ok, sync, 'live'), { tone: 'success', label: 'All systems operational', detail: null })
  assert.equal(controlPlane(null, null, 'connecting').label, 'Checking status')
  assert.deepEqual(controlPlane(ok, { ...sync, openAlerts: 2 }, 'live'), { tone: 'warning', label: 'Degraded', detail: '2 sync alerts open' })
  assert.equal(controlPlane(ok, { ...sync, connectorWide: true }, 'live').detail, 'A connector is failing')
  assert.equal(controlPlane(ok, sync, 'reconnecting').tone, 'warning')
  assert.deepEqual(controlPlane({ ...ok, status: 'Unhealthy', poller: { state: 'stalled' } }, sync, 'live'), { tone: 'danger', label: 'Service disruption', detail: 'Sync poller stalled' })
  assert.equal(controlPlane('unreachable', sync, 'live').detail, 'The server is not answering')
  assert.equal(controlPlane(ok, sync, 'offline').tone, 'danger')
})

test('the user card: initials and the role in words', () => {
  assert.equal(initials('Jordan Lee'), 'JL')
  assert.equal(initials('Ana María de la Cruz'), 'AC')
  assert.equal(initials('rte@example.com'), 'R')
  assert.equal(roleName('ReleaseManager'), 'Release manager')
  assert.equal(roleName('GovernanceOfficer'), 'Governance officer')
})
