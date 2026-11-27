import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, primaryNav, signIn } from './support'

// REOS-179 (D56, D63): the CAB workbench. It exists only while the CAB module is on (module off: the page and routes are absent and the API answers 404). On, a chair runs a meeting from one
// screen: agenda order from the keyboard, flags next to each item, Approve / Reject / Defer with a required comment, separation-of-duties refusals in words, who is connected (live), and a
// Complete meeting that can no longer be edited. Two people deciding the same item get a 409 with the current row. The shared demo database must end with the CAB module off.

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

const W1 = '2033-04-05T02:00:00Z', W2 = '2033-04-05T04:00:00Z'
let n = 0
const unique = (what: string) => `${what} ${Date.now().toString(36)}${++n}`

// Whatever a test did or failed to do, the next spec starts with the module off: a separate session switches it off.
test.afterEach(async ({ browser }) => {
  const ctx = await browser.newContext()
  try {
    const page = await ctx.newPage()
    await signIn(page, 'wb-cleanup-rte@example.com', 'RTE')
    await setModule(page, false)
  } finally { await ctx.close() }
})

/** A person in a context of their own (the shared demo database has one fixed user per email). */
async function person(browser: import('@playwright/test').Browser, email: string, role: string, opts: { blockLive?: boolean } = {}) {
  const ctx = await browser.newContext()
  const page = await ctx.newPage()
  if (opts.blockLive) await page.route('**/hub/**', r => r.abort())   // no live pushes: the screen stays as loaded, which is what a second person with a slow connection sees
  await signIn(page, email, role)
  const me = (await ok(api(page, 'GET', '/api/v1/me'))) as { id: string }
  return { ctx, page, id: me.id }
}

async function world(browser: import('@playwright/test').Browser, tag: string) {
  const rte = await person(browser, `wb-${tag}-rte@example.com`, 'RTE')
  const gov = await person(browser, `wb-${tag}-gov@example.com`, 'GovernanceOfficer')
  const rm = await person(browser, `wb-${tag}-rm@example.com`, 'ReleaseManager', { blockLive: tag === 'race' })
  await setModule(rte.page, true)
  return { rte, gov, rm }
}

async function raise(page: Page, title: string, requestedByUserId?: string) {
  const c = await ok(api(page, 'POST', '/api/v1/cab/changes', { title, changeType: 'Normal', rollbackNote: 'Redeploy the previous build', plannedStartAt: W1, plannedEndAt: W2, requestedByUserId }))
  return (await ok(api(page, 'POST', `/api/v1/cab/changes/${c.id}:submit`, {}, c.version))) as { id: string; key: string; title: string }
}

/** A meeting chaired by the RTE with the RM and the Governance Officer invited, and the given changes on its agenda (in order). */
async function meetingWith(rte: Page, people: { rteId: string; rmId: string; govId: string }, changes: { id: string }[], opts: { start?: boolean } = {}) {
  let m = await ok(api(rte, 'POST', '/api/v1/cab/meetings', { title: unique('E2E CAB'), startsAt: W1, endsAt: W2, chairUserId: people.rteId, location: 'Room 4' }))
  for (const userId of [people.rmId, people.govId]) m = await ok(api(rte, 'POST', `/api/v1/cab/meetings/${m.id}/attendees`, { userId }, m.version))
  for (const c of changes) m = await ok(api(rte, 'POST', `/api/v1/cab/meetings/${m.id}/agenda`, { changeRequestId: c.id }, m.version))
  if (opts.start) m = await ok(api(rte, 'POST', `/api/v1/cab/meetings/${m.id}:start`, {}, m.version))
  return m as { id: string; title: string; version: number }
}

const agendaRow = (page: Page, key: string) => page.getByTestId(`agenda-${key}`)
const stillToDecide = (page: Page) => page.getByRole('list', { name: 'Still to decide' })
const openWorkbench = async (page: Page, id: string, title: string) => { await openPage(page, `/cab/meetings/${id}`); await expect(page.getByRole('heading', { name: title, level: 1 })).toBeVisible() }

test('module off: the meetings pages are Page not found, no navigation entry, and the meeting routes answer 404', async ({ page }) => {
  await signIn(page, 'wb-off-rte@example.com', 'RTE')
  await setModule(page, false)
  await page.reload()
  await expect(primaryNav(page).getByRole('link', { name: 'Approvals' })).toBeVisible()
  await expect(primaryNav(page).getByRole('link', { name: 'Change advisory board' })).toHaveCount(0)
  for (const path of ['/cab/meetings', '/cab/meetings/some-meeting']) {
    await page.goto(path)
    await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible()
  }
  for (const [method, url] of [['GET', '/api/v1/cab/meetings'], ['GET', '/api/v1/cab/meetings/x'], ['GET', '/api/v1/cab/meetings/x/presence'], ['POST', '/api/v1/cab/meetings/x/agenda/y:move'], ['POST', '/api/v1/cab/meetings/x:start']] as const) {
    expect((await api(page, method, url, method === 'POST' ? {} : undefined)).status, `${method} ${url}`).toBe(404)
  }
  expect((await api(page, 'GET', '/api/v1/trains')).status).toBe(200)
})

test('a chair runs a meeting from the keyboard: reorder, flags, comment rules, a refusal in words, who is connected, and Complete locks it', async ({ browser }) => {
  const { rte, gov, rm } = await world(browser, 'run')
  try {
    const a = await raise(rte.page, unique('Alpha'), rm.id)     // keyed in by the chair on the RM's behalf: the chair rule applies
    const b = await raise(rte.page, unique('Bravo'), rm.id)
    const c = await raise(rte.page, unique('Charlie'))          // requested by the chair: the chair cannot decide it at all
    const m = await meetingWith(rte.page, { rteId: rte.id, rmId: rm.id, govId: gov.id }, [a, b, c])

    // the meetings list opens the workbench
    await openPage(rte.page, '/cab/meetings')
    await rte.page.getByRole('table', { name: 'CAB meetings' }).getByText(m.title).click()
    await expect(rte.page.getByRole('heading', { name: m.title, level: 1 })).toBeVisible()
    await expect(rte.page.getByText('Not started')).toBeVisible()

    // flags: all three changes share one window, so each says what it overlaps (a chip with words)
    await expect(agendaRow(rte.page, a.key).getByText(`Overlaps ${b.key}, ${c.key}`)).toBeVisible()

    // reorder with the keyboard: focus the button, press Enter
    const items = () => stillToDecide(rte.page).locator('li')
    await expect(items().first()).toContainText(a.key)
    const down = rte.page.getByRole('button', { name: `Move ${a.key} down` })
    await down.focus()
    await rte.page.keyboard.press('Enter')
    await expect(items().first()).toContainText(b.key)
    await expect(items().nth(1)).toContainText(a.key)
    await rte.page.getByRole('button', { name: `Skip ${b.key} to the end of the agenda` }).click()
    await expect(items().last()).toContainText(b.key)
    await rte.page.getByRole('button', { name: `Take ${b.key} next` }).click()
    await expect(items().first()).toContainText(b.key)

    // nothing can be decided before the meeting is started
    await expect(rte.page.getByText('Decisions are recorded while the meeting is in progress')).toBeVisible()
    await rte.page.getByRole('button', { name: 'Start meeting' }).click()
    await expect(rte.page.getByText('In progress', { exact: true }).first()).toBeVisible()

    // the second person's screen shows who is connected, live
    await openWorkbench(gov.page, m.id, m.title)
    const roll = (page: Page, name: string) => page.getByTestId(`attendee-${name}`)
    await expect(roll(rte.page, `wb-run-gov`).getByText('Connected', { exact: true })).toBeVisible({ timeout: 15_000 })
    await expect(roll(rte.page, `wb-run-rm`).getByText('Connected', { exact: true })).toBeVisible()   // signed in somewhere, so connected
    await rm.ctx.close()                                                                             // and when they leave, every roll says so
    await expect(roll(rte.page, `wb-run-rm`).getByText('Not connected')).toBeVisible({ timeout: 15_000 })

    // approve and reject need a comment, said in words next to the disabled button
    await agendaRow(rte.page, a.key).getByRole('button').first().click()
    await expect(rte.page.getByText('An approval needs a comment.')).toBeVisible()
    await expect(rte.page.getByRole('button', { name: 'Approve' })).toBeDisabled()
    await rte.page.getByLabel('Comment').fill('Rollback was rehearsed')
    // the chair keyed this change in: approving it alone is refused by the second rule, in the rule's words
    await rte.page.getByRole('button', { name: 'Approve' }).click()
    await expect(rte.page.getByText('Refused by a separation-of-duties rule')).toBeVisible()
    await expect(rte.page.getByText(/cannot be the only approver/i).first()).toBeVisible()

    // the change the chair requested cannot be approved or rejected by them: off, with the reason
    await agendaRow(rte.page, c.key).getByRole('button').first().click()
    await expect(rte.page.getByText('You requested this change, so you cannot approve or reject it.').first()).toBeVisible()
    await expect(rte.page.getByRole('button', { name: 'Reject' })).toBeDisabled()

    // the Governance Officer approves A; the chair's screen moves it to Decided without a reload, and the record holds the comment
    await agendaRow(gov.page, a.key).getByRole('button').first().click()
    await gov.page.getByLabel('Comment').fill('Approved by governance')
    await gov.page.getByRole('button', { name: 'Approve' }).click()
    await expect(rte.page.getByRole('list', { name: 'Decided' }).getByTestId(`agenda-${a.key}`)).toBeVisible()
    await expect(rte.page.getByTestId('record-row').filter({ hasText: a.key })).toContainText('Approved by governance')

    // reject asks first; deferring needs no comment
    await agendaRow(gov.page, b.key).getByRole('button').first().click()
    await gov.page.getByLabel('Comment').fill('Not ready: no test evidence')
    await gov.page.getByRole('button', { name: 'Reject' }).click()
    const dialog = gov.page.getByRole('dialog', { name: `Reject ${b.key}?` })
    await expect(dialog).toBeVisible()
    await dialog.getByRole('button', { name: 'Reject change' }).click()
    await expect(gov.page.getByRole('list', { name: 'Decided' }).getByTestId(`agenda-${b.key}`)).toBeVisible()
    await agendaRow(gov.page, c.key).getByRole('button').first().click()
    await gov.page.getByLabel('Comment').fill('')
    await gov.page.getByRole('button', { name: 'Defer' }).click()
    await expect(gov.page.getByTestId('record-row').filter({ hasText: c.key })).toContainText('Deferred')

    // the timer is a control, not a surprise: Start, Pause
    await rte.page.getByRole('button', { name: 'Start timer' }).click()
    await expect(rte.page.getByRole('button', { name: 'Pause timer' })).toBeVisible()
    await rte.page.getByRole('button', { name: 'Pause timer' }).click()

    // complete: the dialog says what is left; afterwards nothing can be edited
    await rte.page.getByRole('button', { name: 'Complete meeting' }).click()
    const done = rte.page.getByRole('dialog', { name: 'Complete this meeting?' })
    await expect(done).toContainText('1 change has no decision yet')
    await done.getByRole('button', { name: 'Complete meeting' }).click()
    await expect(rte.page.getByText('Meeting complete')).toBeVisible()
    await expect(done).toBeHidden()
    await expect(rte.page.getByText('can no longer be edited')).toBeVisible()
    await expect(rte.page.getByRole('button', { name: 'Complete meeting' })).toHaveCount(0)
    await expect(rte.page.getByRole('button', { name: /^Move /})).toHaveCount(0)
    await expect(rte.page.getByRole('button', { name: 'Defer', exact: true })).toHaveCount(0)
    await expect(gov.page.getByText('Meeting complete')).toBeVisible()   // pushed to the other screen
    const now = await ok(api(gov.page, 'GET', `/api/v1/cab/changes/${c.id}`))
    const late = await api(gov.page, 'POST', `/api/v1/cab/changes/${c.id}/decisions`, { decision: 'Approved', rationale: 'Late', meetingId: m.id }, now.version)
    expect(late.status).toBe(422)
    expect(late.json.guard).toBe('CabMeetingClosed')
  } finally { await Promise.all([rte.ctx.close(), gov.ctx.close(), rm.ctx.close()]) }
})

test('two people deciding the same item: the second gets a 409 in words, with the current row', async ({ browser }) => {
  const { rte, gov, rm } = await world(browser, 'race')   // the RM's screen gets no live pushes, so it is still showing the item as pending
  try {
    const a = await raise(rte.page, unique('Delta'))
    const m = await meetingWith(rte.page, { rteId: rte.id, rmId: rm.id, govId: gov.id }, [a], { start: true })
    await openWorkbench(rm.page, m.id, m.title)
    await openWorkbench(gov.page, m.id, m.title)
    await expect(rm.page.getByRole('button', { name: 'Approve' })).toBeVisible()

    await gov.page.getByLabel('Comment').fill('Fine by governance')
    await gov.page.getByRole('button', { name: 'Approve' }).click()
    await expect(gov.page.getByRole('list', { name: 'Decided' }).getByTestId(`agenda-${a.key}`)).toBeVisible()

    await rm.page.getByLabel('Comment').fill('Fine by the release manager')
    await rm.page.getByRole('button', { name: 'Reject' }).click()
    await rm.page.getByRole('dialog', { name: `Reject ${a.key}?` }).getByRole('button', { name: 'Reject change' }).click()
    await expect(rm.page.getByText('Someone else changed this first')).toBeVisible()
    await expect(rm.page.getByRole('list', { name: 'Decided' }).getByTestId(`agenda-${a.key}`)).toBeVisible()   // the screen now shows their decision
    await expect(rm.page.getByTestId('record-row')).toHaveCount(1)
  } finally { await Promise.all([rte.ctx.close(), gov.ctx.close(), rm.ctx.close()]) }
})

test('axe: the meetings list, the workbench and its dialogs are clean in light and dark', async ({ browser }) => {
  const { rte, gov, rm } = await world(browser, 'axe')
  try {
    const a = await raise(rte.page, unique('Echo'), rm.id)
    const b = await raise(rte.page, unique('Foxtrot'), rm.id)
    const m = await meetingWith(rte.page, { rteId: rte.id, rmId: rm.id, govId: gov.id }, [a, b], { start: true })
    for (const scheme of ['light', 'dark'] as const) {
      await rte.page.emulateMedia({ colorScheme: scheme })
      await openPage(rte.page, '/cab/meetings')
      await expect(rte.page.getByRole('table', { name: 'CAB meetings' })).toBeVisible()
      await scan(rte.page, `meetings list (${scheme})`)
      await openWorkbench(rte.page, m.id, m.title)
      await agendaRow(rte.page, a.key).getByRole('button').first().click()
      await rte.page.getByLabel('Comment').fill('Reviewed')
      await scan(rte.page, `workbench (${scheme})`)
      await rte.page.getByRole('button', { name: 'Complete meeting' }).first().click()
      await expect(rte.page.getByRole('dialog', { name: 'Complete this meeting?' })).toBeVisible()
      await scan(rte.page, `complete dialog (${scheme})`)
      await rte.page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()
    }
    await rte.page.getByRole('button', { name: 'Complete meeting' }).first().click()
    await rte.page.getByRole('dialog', { name: 'Complete this meeting?' }).getByRole('button', { name: 'Complete meeting' }).click()
    await expect(rte.page.getByText('Meeting complete')).toBeVisible()
    for (const scheme of ['light', 'dark'] as const) {
      await rte.page.emulateMedia({ colorScheme: scheme })
      await scan(rte.page, `completed workbench (${scheme})`)
    }
  } finally { await Promise.all([rte.ctx.close(), gov.ctx.close(), rm.ctx.close()]) }
})
