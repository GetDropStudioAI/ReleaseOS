import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, primaryNav, signIn } from './support'

// REOS-176 (D56, D63): the change request screens of the CAB module. They exist only while the module is enabled (module off: no navigation entry, Page not found, the API answers 404). On, a change
// is raised, read and moved in the redesign layout, and the screen says in words why it has its risk level (every rule, applied or not), who may not decide it and why, and what a 409 or a
// refusal means. The shared demo database must end with the CAB module off: later specs run with the modules off.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

interface Reply { status: number; json: any }
/** One API call through the signed-in page's own session. */
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
const enableCab = (page: Page) => setModule(page, true)

const W1 = '2033-03-05T02:00:00Z', W2 = '2033-03-05T04:00:00Z'
let n = 0
const unique = (what: string) => `${what} ${Date.now().toString(36)}${++n}`
const draft = (page: Page, over: Record<string, unknown> = {}) =>
  ok(api(page, 'POST', '/api/v1/cab/changes', { title: unique('E2E change'), changeType: 'Normal', rollbackNote: 'Redeploy the previous build', plannedStartAt: W1, plannedEndAt: W2, ...over }))
const submitted = async (page: Page, over: Record<string, unknown> = {}) => {
  const c = await draft(page, over)
  return ok(api(page, 'POST', `/api/v1/cab/changes/${c.id}:submit`, {}, c.version))
}
const cabEntry = (page: Page) => primaryNav(page).getByRole('link', { name: 'Change advisory board' })
const changeTable = (page: Page) => page.getByRole('table', { name: 'Change requests' })
const factor = (page: Page, rule: string) => page.locator(`[data-rule="${rule}"]`)
const openChange = async (page: Page, id: string) => { await openPage(page, `/cab/${id}`); await expect(page.getByRole('heading', { name: 'Why this risk level' })).toBeVisible() }

// Whatever a test did or failed to do, the next spec starts with the module off: a separate session switches it off.
test.afterEach(async ({ browser }) => {
  const ctx = await browser.newContext()
  try {
    const page = await ctx.newPage()
    await signIn(page, 'cab-cleanup-rte@example.com', 'RTE')
    await setModule(page, false)
  } finally { await ctx.close() }
})

/** A second person in a context of their own (the shared demo database has one fixed user per email). */
async function asOther<T>(page: Page, email: string, role: string, fn: (other: Page) => Promise<T>): Promise<T> {
  const ctx = await page.context().browser()!.newContext()
  try {
    const other = await ctx.newPage()
    await signIn(other, email, role)
    return await fn(other)
  } finally { await ctx.close() }
}

test('module off: no navigation entry, Page not found, and every CAB route answers 404 (the rest of the app is unchanged)', async ({ page }) => {
  await signIn(page, 'cab-off-rte@example.com', 'RTE')
  await setModule(page, false)
  await page.reload()
  await expect(primaryNav(page).getByRole('link', { name: 'Approvals' })).toBeVisible()
  await expect(cabEntry(page)).toHaveCount(0)
  for (const path of ['/cab', '/cab/new', '/cab/some-change']) {
    await page.goto(path)
    await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible()
  }
  for (const [method, url] of [['GET', '/api/v1/cab/changes'], ['GET', '/api/v1/cab/risk-rules'], ['GET', '/api/v1/cab/changes/x'], ['POST', '/api/v1/cab/changes'], ['GET', '/api/v1/cab/meetings']] as const) {
    expect((await api(page, method, url, method === 'POST' ? {} : undefined)).status, `${method} ${url}`).toBe(404)
  }
  expect((await api(page, 'GET', '/api/v1/trains')).status).toBe(200)
  await asOther(page, 'cab-off-viewer@example.com', 'Viewer', async v => { await v.goto('/cab'); await expect(v.getByRole('heading', { name: 'Page not found' })).toBeVisible() })
})

test('the Enable button asks first; once enabled the entry appears and the page shows the rule set in words', async ({ page }) => {
  await signIn(page, 'cab-enable-rte@example.com', 'RTE')
  await setModule(page, false)
  await openPage(page, '/admin?tab=modules')
  await expect(cabEntry(page)).toHaveCount(0)
  await page.getByRole('button', { name: 'Enable CAB module' }).click()
  const dialog = page.getByRole('dialog', { name: 'Enable CAB module?' })
  await dialog.getByRole('button', { name: 'Enable CAB module' }).click()
  await expect(cabEntry(page)).toBeVisible()
  expect(await page.getByRole('switch').count() + await page.getByRole('checkbox').count()).toBe(0)
  await cabEntry(page).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Change advisory board' })).toBeVisible()
  await expect(page.getByText('This screen is not built yet')).toHaveCount(0)
  const rules = page.getByRole('table', { name: 'Risk rules' })
  await expect(rules.locator('tbody tr')).toHaveCount(5)
  for (const label of ['Emergency change', 'Change inside or near a freeze', 'No rollback note', 'Prior failed change on the same service', 'Large scope']) await expect(rules).toContainText(label)
  await expect(page.getByText('Score 0 is Low, 1 to 2 Medium, 3 to 4 High, 5 or more Critical.').first()).toBeVisible()
})

test('raise an Emergency change with no rollback note: the screen lists each rule that applied and why, and the level follows the list; adding a note lowers it', async ({ page }) => {
  await signIn(page, 'cab-raise-rte@example.com', 'RTE')
  await enableCab(page)
  await page.reload()
  await cabEntry(page).click()
  await page.getByRole('button', { name: 'New change request' }).first().click()
  await expect(page.getByRole('heading', { level: 1, name: 'New change request' })).toBeVisible()
  const title = unique('Hotfix the gateway')
  await expect(page.getByRole('button', { name: 'Raise change request' })).toBeDisabled()
  await expect(page.getByText('Give the change a title.')).toBeVisible()      // the reason the button is off, in words
  await page.getByLabel('Title').fill(title)
  await page.getByLabel('Type').selectOption('Emergency')
  await page.getByLabel('Planned start').fill('2033-03-05T02:00')
  await expect(page.getByText(/UTC/).first()).toBeVisible()                  // the zone note says what the typed time comes to in UTC
  await page.getByLabel('Planned end').fill('2033-03-05T01:00')
  await expect(page.getByText('The planned window must end after it starts.')).toBeVisible()
  await page.getByLabel('Planned end').fill('2033-03-05T03:00')
  await page.getByRole('button', { name: 'Raise change request' }).click()

  await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Why this risk level' })).toBeVisible()
  await expect(page.getByText('Critical risk', { exact: true }).first()).toBeVisible()        // a word, not just a colour
  await expect(page.getByTestId('risk-sentence')).toHaveText('Critical risk: Emergency change (+3), No rollback note (+2). Score 5.')
  for (const rule of ['Emergency', 'NoRollbackNote']) { await expect(factor(page, rule)).toHaveAttribute('data-applied', 'true'); await expect(factor(page, rule)).toContainText(/Applied, \+\d/) }
  for (const rule of ['FreezeWindow', 'PriorFailure', 'LargeScope']) { await expect(factor(page, rule)).toHaveAttribute('data-applied', 'false'); await expect(factor(page, rule)).toContainText('Not applied') }
  await expect(factor(page, 'Emergency')).toContainText('may be implemented before the CAB approves it')
  await expect(page.locator('main').getByText(/UTC/).first()).toBeVisible()

  // a Draft is edited; saving runs the rules again and the list follows
  await page.getByRole('button', { name: 'Edit details' }).click()
  await page.getByLabel('Rollback note').fill('Redeploy the previous build from the pipeline')
  await page.getByRole('button', { name: 'Save changes' }).click()
  await expect(page.getByTestId('risk-sentence')).toHaveText('High risk: Emergency change (+3). Score 3.')
  await expect(factor(page, 'NoRollbackNote')).toHaveAttribute('data-applied', 'false')
  await expect(factor(page, 'NoRollbackNote')).toContainText('A rollback note is recorded.')
  await expect(page.getByTestId('announcer')).toContainText('Risk is High')
  await expect(page.getByRole('button', { name: 'Edit details' })).toBeFocused()              // focus comes back to where the edit started

  await cabEntry(page).click()
  await expect(changeTable(page).locator('tbody tr', { hasText: title })).toContainText('High risk')
})

test('a freeze window over the planned window is named in the factor list, in UTC', async ({ page }) => {
  await signIn(page, 'cab-freeze-rm@example.com', 'ReleaseManager')   // creating a freeze window is for a Release Manager
  await enableCab(page)
  const name = unique('CAB e2e freeze')
  await ok(api(page, 'POST', '/api/v1/freezes', { name, kind: 'Freeze', startsAt: '2034-06-10T00:00:00Z', endsAt: '2034-06-10T06:00:00Z', productPattern: null }))   // its own year: no other test's window is near it
  const c = await draft(page, { plannedStartAt: '2034-06-10T02:00:00Z', plannedEndAt: '2034-06-10T04:00:00Z' })
  expect(c.riskLevel).toBe('High')
  await openChange(page, c.id)
  await expect(page.getByText('High risk', { exact: true }).first()).toBeVisible()
  await expect(factor(page, 'FreezeWindow')).toHaveAttribute('data-applied', 'true')
  await expect(factor(page, 'FreezeWindow')).toContainText(`overlaps the freeze window "${name}"`)
  await expect(factor(page, 'FreezeWindow')).toContainText('2034-06-10 00:00 UTC')
  await expect(page.getByTestId('risk-sentence')).toContainText('Change inside or near a freeze (+3)')
})

test('the requester is told why they cannot approve; a Governance Officer can; an Emergency change cannot be closed until a second person has reviewed it, and the refusal is in words', async ({ page }) => {
  await signIn(page, 'cab-sod-rte@example.com', 'RTE')
  await enableCab(page)
  const c = await submitted(page)
  await openChange(page, c.id)
  await expect(page.getByRole('heading', { name: 'Separation of duties' })).toBeVisible()
  await expect(page.getByText('You cannot approve or reject a change you requested. Deferring is allowed.')).toBeVisible()
  await expect(page.getByTestId('self-requester')).toContainText('You requested this change, so you cannot approve or reject it')
  const record = page.getByRole('button', { name: 'Record decision' })
  await expect(record).toBeDisabled()
  await expect(record).toHaveAccessibleDescription(/you cannot approve or reject it/)
  await page.getByRole('combobox', { name: /^Decision/ }).selectOption('Rejected')
  await expect(record).toBeDisabled()
  await page.getByRole('combobox', { name: /^Decision/ }).selectOption('Deferred')               // deferring is not a verdict
  await page.getByLabel('Rationale').fill('Need the rollback rehearsed first')
  await expect(record).toBeEnabled()
  await record.click()
  await expect(page.getByRole('heading', { name: 'Decisions' }).locator('xpath=ancestor::section[1]')).toContainText('Deferred')
  await expect(page.getByText('Submitted', { exact: true }).first()).toBeVisible()

  // another person approves; the page shows it after a reload (and live, through CabChanged)
  const now = await ok(api(page, 'GET', `/api/v1/cab/changes/${c.id}`))
  await asOther(page, 'cab-sod-gov@example.com', 'GovernanceOfficer', async gov => {
    await ok(api(gov, 'POST', `/api/v1/cab/changes/${c.id}/decisions`, { decision: 'Approved', rationale: 'Rehearsal passed' }, now.version))
  })
  await expect(page.getByText('Approved', { exact: true }).first()).toBeVisible()
  await expect(page.getByRole('button', { name: 'Mark implemented' })).toBeVisible()

  // an Emergency change: implemented straight from Submitted, then it cannot be closed without a second person's review
  const e = await submitted(page, { changeType: 'Emergency', title: unique('Emergency fix') })
  await openChange(page, e.id)
  await page.getByRole('button', { name: 'Implement now' }).click()
  await expect(page.getByText("Waiting for a second person's review")).toBeVisible()
  await page.getByRole('button', { name: 'Close change' }).click()
  const refusal = page.getByText('Refused by a separation-of-duties rule').locator('xpath=ancestor::div[contains(@class,"os-notice")][1]')
  await expect(refusal).toContainText('Separation of duties: an Emergency change needs a review by a second person before it can be closed')
  await expect(refusal).toContainText('ChangeEmergencyNeedsReview')
  const reviewed = await ok(api(page, 'GET', `/api/v1/cab/changes/${e.id}`))
  await asOther(page, 'cab-sod-gov@example.com', 'GovernanceOfficer', async gov => {
    await ok(api(gov, 'POST', `/api/v1/cab/changes/${e.id}/decisions`, { decision: 'Approved', rationale: 'Reviewed after the fact', retroactive: true }, reviewed.version))
  })
  await page.reload()
  await expect(page.getByText('Retroactive review')).toBeVisible()
  await page.getByRole('button', { name: 'Close change' }).click()
  await expect(page.getByText('Closed', { exact: true }).first()).toBeVisible()
})

test('409: someone else saved first; the screen says so, keeps what was typed, and offers their version or an overwrite', async ({ page }) => {
  await signIn(page, 'cab-409-rte@example.com', 'RTE')
  await enableCab(page)
  const c = await draft(page)
  await openChange(page, c.id)
  await page.getByRole('button', { name: 'Edit details' }).click()
  await page.getByLabel('Title').fill('My title')
  const theirs = await ok(api(page, 'PATCH', `/api/v1/cab/changes/${c.id}`, { title: 'Their title' }, c.version))
  await expect(page.getByText('This change request changed while you were editing')).toBeVisible()   // the live push tells the editor; the form and its version are left alone
  await page.getByRole('button', { name: 'Save changes' }).click()
  await expect(page.getByText('Someone else changed this first')).toBeVisible()
  await expect(page.getByLabel('Title')).toHaveValue('My title')              // the draft is kept
  await page.getByRole('button', { name: 'Overwrite' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'My title' })).toBeVisible()
  await expect(page.getByText('Someone else changed this first')).toHaveCount(0)
  const row = await ok(api(page, 'GET', `/api/v1/cab/changes/${c.id}`))
  expect(row.title).toBe('My title')
  expect(row.version).toBe(theirs.version + 1)

  // use theirs
  await page.getByRole('button', { name: 'Edit details' }).click()
  await page.getByLabel('Title').fill('Another title of mine')
  await ok(api(page, 'PATCH', `/api/v1/cab/changes/${c.id}`, { title: 'Their second title' }, row.version))
  await page.getByRole('button', { name: 'Save changes' }).click()
  await expect(page.getByText('Someone else changed this first')).toBeVisible()
  await page.getByRole('button', { name: 'Use theirs' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Their second title' })).toBeVisible()
  await expect(page.getByLabel('Title')).toHaveCount(0)

  // a move with a stale version is a 409 as well (the live refresh is held back, so the page still holds the old version when the button is pressed)
  const current = await ok(api(page, 'GET', `/api/v1/cab/changes/${c.id}`))
  await page.route(`**/api/v1/cab/changes/${c.id}`, r => r.request().method() === 'GET' ? r.abort() : r.continue())
  await ok(api(page, 'PATCH', `/api/v1/cab/changes/${c.id}`, { description: 'changed meanwhile' }, current.version))
  await page.getByRole('button', { name: 'Submit to the CAB' }).click()
  await expect(page.getByText('Someone else changed this first')).toBeVisible()
  await expect(page.getByText('Draft', { exact: true }).first()).toBeVisible()      // nothing was submitted
})

test('a Viewer reads everything and changes nothing: no New button, no Edit, no moves, no decision form, and the API says 403', async ({ page }) => {
  await signIn(page, 'cab-view-rte@example.com', 'RTE')
  await enableCab(page)
  const c = await submitted(page)
  await asOther(page, 'cab-view-viewer@example.com', 'Viewer', async v => {
    await v.goto('/')
    await expect(cabEntry(v)).toBeVisible()
    await cabEntry(v).click()
    await expect(v.getByRole('heading', { level: 1, name: 'Change advisory board' })).toBeVisible()
    await expect(v.getByText('Read only')).toBeVisible()
    await expect(v.getByRole('button', { name: 'New change request' })).toHaveCount(0)
    await expect(changeTable(v)).toBeVisible()
    await v.goto(`/cab/${c.id}`)
    await expect(v.getByRole('heading', { name: 'Why this risk level' })).toBeVisible()
    await expect(v.getByText('Read only')).toBeVisible()
    for (const name of ['Edit details', 'Submit to the CAB', 'Cancel change', 'Record decision', 'Implement now']) await expect(v.getByRole('button', { name })).toHaveCount(0)
    await v.goto('/cab/new')
    await expect(v.getByText('You cannot raise a change request')).toBeVisible()
    expect((await api(v, 'POST', '/api/v1/cab/changes', { title: 'x', changeType: 'Normal' })).status).toBe(403)
    expect((await api(v, 'POST', `/api/v1/cab/changes/${c.id}/decisions`, { decision: 'Approved' }, c.version)).status).toBe(403)
    expect((await api(v, 'GET', '/api/v1/cab/changes')).status).toBe(200)
  })
})

test('keyboard: the list, the row, the form and the confirmation dialog can all be used without a mouse', async ({ page }) => {
  await signIn(page, 'cab-keys-rte@example.com', 'RTE')
  await enableCab(page)
  const c = await draft(page)
  await openPage(page, '/cab')
  await expect(changeTable(page).locator('tbody tr', { hasText: c.key }).first()).toBeVisible()
  const newBtn = page.getByRole('button', { name: 'New change request' }).first()
  await newBtn.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('heading', { level: 1, name: 'New change request' })).toBeVisible()
  await page.getByLabel('Title').focus()
  await page.keyboard.type('Typed with the keyboard')
  await page.keyboard.press('Enter')                                          // Enter in a text box submits the form
  await expect(page.getByRole('heading', { level: 1, name: 'Typed with the keyboard' })).toBeVisible()

  await openPage(page, '/cab')
  const row = changeTable(page).locator('tbody tr', { hasText: c.key }).first()
  await row.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('heading', { name: 'Why this risk level' })).toBeVisible()

  const cancel = page.getByRole('button', { name: 'Cancel change' })
  await cancel.focus()
  await page.keyboard.press('Enter')
  const dialog = page.getByRole('dialog', { name: 'Cancel this change request?' })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Cancel', exact: true })).toBeFocused()   // a destructive dialog starts on Cancel
  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)
  await expect(cancel).toBeFocused()
  await cancel.press('Enter')
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel change' }).click()
  await expect(page.getByText('Cancelled', { exact: true }).first()).toBeVisible()
})

test('loading, empty and error states are in words; a missing change is "not found"', async ({ page }) => {
  await signIn(page, 'cab-states-rte@example.com', 'RTE')
  await enableCab(page)
  await openPage(page, '/cab/does-not-exist')
  await expect(page.getByRole('heading', { level: 1, name: 'Change request not found' })).toBeVisible()
  await page.getByRole('button', { name: 'Back to the list' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Change advisory board' })).toBeVisible()
  // the list could not be read
  await page.route('**/api/v1/cab/changes?*', r => r.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ title: 'forced', message: 'The changes could not be read' }) }))
  await page.route('**/api/v1/cab/changes', r => r.request().method() === 'GET' ? r.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ title: 'forced', message: 'The changes could not be read' }) }) : r.continue())
  await page.reload()
  await expect(page.getByText('Change requests could not be loaded')).toBeVisible()
  await expect(page.getByText('The changes could not be read')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Retry' })).toBeVisible()
  // filters with no match
  await page.unroute('**/api/v1/cab/changes?*'); await page.unroute('**/api/v1/cab/changes')
  await page.reload()
  await page.getByLabel('Status').selectOption('Closed')
  await page.getByLabel('Type').selectOption('Standard')
  await expect(page.getByText('No change request matches these filters.')).toBeVisible()
})

test('axe: list, new form, detail with risk factors, edit form and the confirmation dialog are clean in light and dark', async ({ page }) => {
  await signIn(page, 'cab-axe-rm@example.com', 'ReleaseManager')
  await enableCab(page)
  const freeze = unique('CAB axe freeze')
  await ok(api(page, 'POST', '/api/v1/freezes', { name: freeze, kind: 'Freeze', startsAt: '2035-09-09T00:00:00Z', endsAt: '2035-09-09T06:00:00Z', productPattern: null }))
  const c = await draft(page, { changeType: 'Emergency', rollbackNote: null, plannedStartAt: '2035-09-09T02:00:00Z', plannedEndAt: '2035-09-09T04:00:00Z' })
  await ok(api(page, 'POST', `/api/v1/cab/changes`, { title: unique('Second'), changeType: 'Normal' }))
  for (const scheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: scheme })
    await openPage(page, '/cab'); await expect(changeTable(page)).toBeVisible()
    await scan(page, `list (${scheme})`)
    await openPage(page, '/cab/new'); await expect(page.getByLabel('Title')).toBeVisible()
    await scan(page, `new (${scheme})`)
    await openChange(page, c.id)
    await expect(factor(page, 'FreezeWindow')).toHaveAttribute('data-applied', 'true')
    await scan(page, `detail (${scheme})`)
    await page.getByRole('button', { name: 'Edit details' }).click()
    await expect(page.getByLabel('Rollback note')).toBeVisible()
    await scan(page, `edit (${scheme})`)
    await page.getByRole('button', { name: 'Cancel', exact: true }).first().click()
    await page.getByRole('button', { name: 'Cancel change' }).click()
    await expect(page.getByRole('dialog', { name: 'Cancel this change request?' })).toBeVisible()
    await scan(page, `cancel dialog (${scheme})`)
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click()
  }
})
