import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, primaryNav, signIn } from './support'

// REOS-180 (D56, D9): conflict, freeze and blackout visibility for changes. A change shows what it collides with, in words and with the rule that fired; the calendar lists changes with a diamond
// marker and a count in words; a conflict never blocks, but approving over one needs a reason. Module off: no UI, API 404. The shared demo database must end with the CAB module off, and the
// holiday this spec adds is removed again (the freeze window cannot be deleted by design: it is in 2034, far from every other spec).

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

interface Reply { status: number; json: any }
async function api(page: Page, method: string, url: string, body?: unknown, ifMatch?: number): Promise<Reply> {
  return page.evaluate(async ([m, u, b, v]) => {
    const headers: Record<string, string> = {}
    if (b !== undefined) headers['Content-Type'] = 'application/json'
    if (v !== undefined) headers['If-Match'] = `"${v}"`
    const r = await fetch(u as string, { method: m as string, headers, body: b === undefined ? undefined : JSON.stringify(b) })
    const text = await r.text()
    let json: unknown = null
    try { json = JSON.parse(text) } catch { json = text }
    return { status: r.status, json }
  }, [method, url, body, ifMatch] as const)
}
const ok = async (p: Promise<Reply>) => { const r = await p; expect(r.status, JSON.stringify(r.json)).toBeLessThan(300); return r.json }

async function setModule(page: Page, on: boolean) {
  const list = (await ok(api(page, 'GET', '/api/v1/modules'))) as { key: string; enabled: boolean; version: number }[]
  const cab = list.find(m => m.key === 'cab')!
  if (cab.enabled !== on) await ok(api(page, 'POST', `/api/v1/modules/cab:${on ? 'enable' : 'disable'}`, {}, cab.version))
}

const DAY = '2034-07-10', START = `${DAY}T02:00:00Z`, END = `${DAY}T04:00:00Z`
let n = 0
const unique = (what: string) => `${what} ${Date.now().toString(36)}${++n}`
const submitted = async (page: Page, over: Record<string, unknown> = {}) => {
  const c = await ok(api(page, 'POST', '/api/v1/cab/changes', { title: unique('E2E conflict change'), changeType: 'Normal', rollbackNote: 'Redeploy the previous build', plannedStartAt: START, plannedEndAt: END, ...over }))
  return ok(api(page, 'POST', `/api/v1/cab/changes/${c.id}:submit`, {}, c.version))
}
const conflictsPanel = (page: Page) => page.getByRole('heading', { name: 'Conflicts', exact: true }).locator('xpath=ancestor::section[1]')

async function asOther<T>(page: Page, email: string, role: string, fn: (other: Page) => Promise<T>): Promise<T> {
  const ctx = await page.context().browser()!.newContext()
  try {
    const other = await ctx.newPage()
    await signIn(other, email, role)
    return await fn(other)
  } finally { await ctx.close() }
}

test.afterEach(async ({ browser }) => {
  const ctx = await browser.newContext()
  try {
    const page = await ctx.newPage()
    await signIn(page, 'cab-conflicts-cleanup@example.com', 'RTE')
    await api(page, 'DELETE', `/api/v1/holidays/${DAY}`)
    await setModule(page, false)
  } finally { await ctx.close() }
})

test('module off: no conflict routes, no calendar overlay, no Changes panel', async ({ page }) => {
  await signIn(page, 'cab-conflicts-off@example.com', 'RTE')
  await setModule(page, false)
  for (const url of ['/api/v1/cab/conflict-rules', '/api/v1/cab/changes/x/conflicts', '/api/v1/cab/meetings/x/conflicts', `/api/v1/cab/calendar?from=${DAY}&to=${DAY}`]) expect((await api(page, 'GET', url)).status, url).toBe(404)
  await openPage(page, '/calendar')
  await expect(page.getByRole('heading', { name: 'Changes in this period' })).toHaveCount(0)
  await expect(primaryNav(page).getByRole('link', { name: 'Change advisory board' })).toHaveCount(0)
})

test('a change shows its freeze and blackout conflicts in words with the rule, approving over them needs a reason, and the calendar marks the change', async ({ page }) => {
  await signIn(page, 'cab-conflicts-rte@example.com', 'RTE')
  await setModule(page, true)
  await asOther(page, 'cab-conflicts-rm@example.com', 'ReleaseManager', async rm => {
    await ok(api(rm, 'POST', '/api/v1/freezes', { name: 'E2E July freeze', kind: 'Freeze', startsAt: `${DAY}T00:00:00Z`, endsAt: '2034-07-11T00:00:00Z' }))
  })
  await api(page, 'POST', '/api/v1/holidays', { day: DAY, name: 'E2E Founders Day' })
  const clear = await submitted(page, { plannedStartAt: '2034-08-10T02:00:00Z', plannedEndAt: '2034-08-10T04:00:00Z' })
  const ch = await submitted(page)

  // the change detail
  await openPage(page, `/cab/${ch.id}`)
  const panel = conflictsPanel(page)
  await expect(panel.getByText('2 conflicts').first()).toBeVisible()        // a word, not just a colour
  await expect(panel.locator('[data-rule="FreezeOverlap"]')).toContainText('Inside a freeze window')
  await expect(panel.locator('[data-rule="FreezeOverlap"]')).toContainText('E2E July freeze')
  await expect(panel.locator('[data-rule="BlackoutDay"]')).toContainText('E2E Founders Day')
  await expect(panel.getByText('A conflict warns and never blocks')).toBeVisible()
  await expect(panel.locator('[data-rule="FreezeOverlap"]').getByText('UTC').first()).toBeVisible()
  await scan(page, 'the change detail with conflicts (light)')
  await page.emulateMedia({ colorScheme: 'dark' }); await scan(page, 'the change detail with conflicts (dark)'); await page.emulateMedia({ colorScheme: 'light' })

  // a second person approves: the button waits for a reason, in words; a short reason is still not enough
  await asOther(page, 'cab-conflicts-gov@example.com', 'GovernanceOfficer', async gov => {
    await openPage(gov, `/cab/${ch.id}`)
    await expect(conflictsPanel(gov).getByText('2 conflicts').first()).toBeVisible()
    await gov.getByRole('combobox', { name: 'Decision' }).selectOption('Approved')
    await expect(gov.getByText('Required: why you are approving over the conflicts shown')).toBeVisible()
    await expect(gov.getByRole('button', { name: 'Record decision' })).toBeDisabled()
    await gov.getByLabel('Rationale').fill('too short')
    await expect(gov.getByRole('button', { name: 'Record decision' })).toBeDisabled()
    await gov.getByLabel('Rationale').fill('Customer-requested fix; the freeze lead agreed in the CAB.')
    await gov.getByRole('button', { name: 'Record decision' }).click()
    await expect(gov.getByTestId('announcer')).toContainText('Approved recorded on')
    await expect(gov.getByText('Approved', { exact: true }).first()).toBeVisible()
    // the service says the same in words when the screen is bypassed
    const refused = await api(gov, 'POST', `/api/v1/cab/changes/${(await submitted(page)).id}/decisions`, { decision: 'Approved', rationale: '' }, 2)
    expect(refused.status).toBe(422)
    expect(refused.json.guard).toBe('ChangeConflictNeedsReason')
  })

  // a change with no conflict needs no reason
  await asOther(page, 'cab-conflicts-gov2@example.com', 'GovernanceOfficer', async gov => {
    await openPage(gov, `/cab/${clear.id}`)
    await expect(conflictsPanel(gov).getByText('No conflicts').first()).toBeVisible()
    await gov.getByRole('combobox', { name: 'Decision' }).selectOption('Approved')
    await expect(gov.getByRole('button', { name: 'Record decision' })).toBeEnabled()
  })
})

test('the calendar overlay lists the change with a diamond marker and the count in words, and is clean in light and dark', async ({ page }) => {
  await signIn(page, 'cab-conflicts-cal@example.com', 'RTE')
  await setModule(page, true)
  // two changes on the same release, today, with overlapping windows: each is a conflict of the other (no freeze or holiday is added, so no other spec is touched)
  const trains = await ok(api(page, 'GET', '/api/v1/trains')) as { id: string }[]
  const today = new Date().toISOString().slice(0, 10)
  const a = await submitted(page, { releaseTrainId: trains[0].id, plannedStartAt: `${today}T12:00:00Z`, plannedEndAt: `${today}T14:00:00Z` })
  await submitted(page, { releaseTrainId: trains[0].id, plannedStartAt: `${today}T13:00:00Z`, plannedEndAt: `${today}T15:00:00Z` })
  await openPage(page, '/calendar')
  const table = page.getByRole('table', { name: 'Changes in this period' })
  const row = table.locator('tr', { hasText: a.key })
  await expect(row).toBeVisible()
  await expect(row.getByText('1 conflict')).toBeVisible()              // the count is a word
  await expect(row.getByText('Another change on the same release, service or environment')).toBeVisible()
  await expect(row.getByText('◆').first()).toBeVisible()               // a shape, not a colour
  await expect(row.getByText('UTC').first()).toBeVisible()
  await scan(page, 'the calendar with the change overlay (light)')
  await page.emulateMedia({ colorScheme: 'dark' })
  await scan(page, 'the calendar with the change overlay (dark)')
  // the row opens the change
  await row.click()
  await expect(page.getByRole('heading', { name: 'Conflicts', exact: true })).toBeVisible()
})
