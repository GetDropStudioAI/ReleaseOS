import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// Q-LC1/Q-LC2: an Executing train is completed through "Complete…" (close code + optional notes, POST :complete with If-Match), never through
// :advance, which has no close code and could only fail. Planning, Gated and Executing trains can be aborted with a reason, kept in the Abort
// audit row. A 409 is shown inline (no modal), keeps what the user chose and offers to apply it to the train as it is now.
// Adds its own trains to the shared demo database: live-drill trains (Executing, no gates) and one blank Planning train.

const stamp = Date.now() % 1_000_000
const day = (n: number) => new Date(Date.now() + n * 86_400_000).toISOString().slice(0, 10)
const header = (page: Page) => page.locator('main .header-line')

/** A new Executing train with no gates, so nothing stands between it and Complete (dev-only endpoint, as live.spec uses). */
const executingTrain = (page: Page) =>
  page.evaluate(async () => (await (await fetch('/api/v1/dev/live-drill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json() as { trainId: string }).trainId)

const train = (page: Page, id: string) => page.evaluate(async i => (await fetch(`/api/v1/trains/${i}`)).json() as Promise<{ status: string; version: number }>, id)

/** Someone else changes the train first: a rollback-rehearsal attestation is a new Version with the same status. */
const changeElsewhere = (page: Page, id: string) => page.evaluate(async i => {
  const t = await (await fetch(`/api/v1/trains/${i}`)).json() as { version: number }
  return (await fetch(`/api/v1/trains/${i}:rehearsed-rollback`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': String(t.version) }, body: '{}' })).status
}, id)

test('an Executing train completes through "Complete…" with a close code, and Unsuccessful brings up the PIR', async ({ page }) => {
  await signIn(page, 'close-rte@example.com')
  const id = await executingTrain(page)
  const posted: string[] = []
  page.on('request', r => { if (r.method() === 'POST' && r.url().includes(`/api/v1/trains/${id}:`)) posted.push(r.url().slice(r.url().lastIndexOf(':'))) })
  await page.goto(`/trains/${id}`)

  const head = header(page)
  await expect(page.locator('section[aria-label=Readiness]')).toContainText('✓ Ready for Complete')
  await expect(head.getByRole('button', { name: /^Advance to/ })).toHaveCount(0)            // the hop that always answered 422 CloseCodeRequired
  await head.getByRole('button', { name: 'Complete…' }).click()
  await expect(head.getByRole('button', { name: 'Cancel complete' })).toHaveAttribute('aria-expanded', 'true')

  // The close code is an explicit choice: underlined words, nothing preselected, Confirm off with the reason below it.
  const form = page.getByRole('group', { name: 'Complete train' })
  const codes = form.getByRole('radio')
  await expect(codes).toHaveText(['Successful', 'Successful with issues', 'Unsuccessful'].map(w => new RegExp(`${w}$`)))
  for (const c of await codes.all()) await expect(c).toHaveAttribute('aria-checked', 'false')
  await expect(form.getByRole('radio', { name: 'Successful', exact: true })).toBeFocused()
  const confirm = form.getByRole('button', { name: 'Confirm complete' })
  await expect(confirm).toBeDisabled()
  await expect(form).toContainText('Confirm is off: choose a close code first.')

  await form.getByRole('radio', { name: 'Unsuccessful' }).click()
  await expect(form.getByRole('radio', { name: 'Unsuccessful' })).toHaveAttribute('aria-checked', 'true')
  await expect(form).toContainText('A post-implementation review will be required')
  await form.getByLabel('Notes (optional)').fill('Rolled back after the smoke test failed')
  const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()
  expect(axe.violations.filter(v => v.impact === 'serious' || v.impact === 'critical').map(v => v.id)).toEqual([])

  const { version } = await train(page, id)
  const [req] = await Promise.all([page.waitForRequest(r => r.url().endsWith(`/api/v1/trains/${id}:complete`)), confirm.click()])
  expect(req.headers()['if-match']).toBe(String(version))
  expect(req.postDataJSON()).toEqual({ closeCode: 'Unsuccessful', notes: 'Rolled back after the smoke test failed' })

  // The trigger made the PIR; the refresh shows it in the Closeout section, and the header has no lifecycle actions left.
  const pir = page.locator('section[aria-label="Post-implementation review"]')
  await expect(pir).toContainText('▲ Required')
  await expect(pir).toContainText('close code Unsuccessful')
  await expect(page.locator('main')).toContainText('Complete · Target')
  await expect(head.getByRole('button', { name: /^(Complete…|Cancel complete|Abort train)$/ })).toHaveCount(0)
  await expect(page.locator('aside[aria-label=Stream] .stream-row[aria-current=true]')).toContainText('Unsuccessful')
  expect((await train(page, id)).status).toBe('Complete')
  expect(posted).toEqual([':complete'])
})

test('a stale complete is shown inline, keeps the close code and notes, and "Complete anyway" applies them', async ({ browser }) => {
  const a = await (await browser.newContext()).newPage()
  const b = await (await browser.newContext()).newPage()
  await b.routeWebSocket(/\/hub\/trains/, () => { /* B never hears the live push: its screen keeps the old version, which is the race */ })
  await signIn(a, 'complete-a@example.com')
  await signIn(b, 'complete-b@example.com', 'ReleaseManager')
  const id = await executingTrain(a)

  await b.goto(`/trains/${id}`)
  await header(b).getByRole('button', { name: 'Complete…' }).click()
  const form = b.getByRole('group', { name: 'Complete train' })
  await form.getByRole('radio', { name: 'Successful with issues' }).click()
  await form.getByLabel('Notes (optional)').fill('One product rolled back')

  expect(await changeElsewhere(a, id)).toBe(200)
  await form.getByRole('button', { name: 'Confirm complete' }).click()
  const alert = b.locator('.conflict')
  await expect(alert).toContainText('complete-a')                                             // who
  await expect(alert).toContainText('changed this train before your change was saved')
  await expect(alert).toContainText('Your change was not applied')
  await expect(alert).toContainText('The train is now Executing')                            // what it is now
  await expect(b.locator('[role=dialog], dialog')).toHaveCount(0)
  expect((await train(a, id)).status).toBe('Executing')
  await expect(form.getByRole('radio', { name: 'Successful with issues' })).toHaveAttribute('aria-checked', 'true')   // the choice is kept
  await expect(form.getByLabel('Notes (optional)')).toHaveValue('One product rolled back')

  await Promise.all([b.waitForResponse(r => r.url().endsWith(`/api/v1/trains/${id}:complete`) && r.status() === 200), alert.getByRole('button', { name: 'Complete anyway' }).click()])
  await expect(b.locator('.conflict')).toHaveCount(0)
  await expect(b.locator('section[aria-label="Post-implementation review"]')).toContainText('close code SuccessfulWithIssues')
  expect((await train(a, id)).status).toBe('Complete')
})

test('abort needs a reason, a stale abort is shown inline, and "Abort anyway" records the same reason in the audit row', async ({ browser }) => {
  const a = await (await browser.newContext()).newPage()
  const b = await (await browser.newContext()).newPage()
  await b.routeWebSocket(/\/hub\/trains/, () => {})
  await signIn(a, 'abort-a@example.com')
  await signIn(b, 'abort-b@example.com')
  const title = `E2E abort ${stamp}`
  const id = await a.evaluate(async body => (await (await fetch('/api/v1/trains', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })).json() as { id: string }).id,
    { title, targetReleaseDate: day(40) })

  await b.goto(`/trains/${id}`)
  await expect(b.locator('main h1')).toHaveText(title)
  const head = header(b)
  await head.getByRole('button', { name: 'Abort train' }).click()
  const confirm = head.getByRole('group', { name: `Abort ${title}? An aborted train cannot be restarted.` })
  const reason = confirm.getByLabel('Reason (required)')
  await expect(reason).toBeFocused()
  await expect(confirm.getByRole('button', { name: 'Abort train' })).toBeDisabled()          // no reason, no abort
  await reason.fill('Scope moved to the next train')

  expect(await changeElsewhere(a, id)).toBe(200)
  await confirm.getByRole('button', { name: 'Abort train' }).click()
  const alert = b.locator('.conflict')
  await expect(alert).toContainText('abort-a')
  await expect(alert).toContainText('Your change was not applied')
  await expect(alert).toContainText('The train is now Planning')
  await expect(b.locator('[role=dialog], dialog')).toHaveCount(0)
  expect((await train(a, id)).status).toBe('Planning')

  const [res] = await Promise.all([b.waitForResponse(r => r.url().endsWith(`/api/v1/trains/${id}:abort`)), alert.getByRole('button', { name: 'Abort anyway' }).click()])
  expect(res.status()).toBe(200)
  expect(res.request().postDataJSON()).toEqual({ reason: 'Scope moved to the next train' })
  await expect(b.locator('main')).toContainText('Aborted · Target')
  await expect(head.getByRole('button', { name: 'Abort train' })).toHaveCount(0)

  // There is no column for the reason: it is in the Abort audit row, with the person who aborted.
  const audit = await a.evaluate(async i => (await (await fetch(`/api/v1/audit?train=${i}&action=Abort`)).json() as { items: { actorName: string | null; afterJson: string | null }[] }).items, id)
  expect(audit).toHaveLength(1)
  expect(audit[0].actorName).toBe('abort-b')
  expect(JSON.parse(audit[0].afterJson ?? '{}')).toMatchObject({ status: 'Aborted', reason: 'Scope moved to the next train' })
})

test('a refused complete keeps its message after the refresh, and once the train has moved on there is no "Complete anyway"', async ({ browser }) => {
  const a = await (await browser.newContext()).newPage()
  const b = await (await browser.newContext()).newPage()
  await b.routeWebSocket(/\/hub\/trains/, () => {})
  await signIn(a, 'moved-a@example.com')
  await signIn(b, 'moved-b@example.com')
  const id = await executingTrain(a)

  await b.goto(`/trains/${id}`)
  await header(b).getByRole('button', { name: 'Complete…' }).click()
  const form = b.getByRole('group', { name: 'Complete train' })
  await form.getByRole('radio', { name: 'Successful', exact: true }).click()
  await form.getByLabel('Notes (optional)').fill('x'.repeat(2001))
  // 422 (FieldTooLong): the write refetches the train, and the message must survive that refetch
  await Promise.all([b.waitForResponse(r => r.url().endsWith(`/api/v1/trains/${id}`) && r.request().method() === 'GET'), form.getByRole('button', { name: 'Confirm complete' }).click()])
  const refused = b.locator('main p.bad[role=alert]', { hasText: 'at most 2,000 are allowed' })
  await expect(refused).toBeVisible()
  await expect(form).toBeVisible()
  expect((await train(a, id)).status).toBe('Executing')

  // Someone else aborts the train meanwhile: B's 409 says so and offers only Dismiss, since completing an aborted train cannot work
  const aborted = await a.evaluate(async i => {
    const t = await (await fetch(`/api/v1/trains/${i}`)).json() as { version: number }
    return (await fetch(`/api/v1/trains/${i}:abort`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': String(t.version) }, body: JSON.stringify({ reason: 'Called off' }) })).status
  }, id)
  expect(aborted).toBe(200)
  await form.getByLabel('Notes (optional)').fill('Done')
  await form.getByRole('button', { name: 'Confirm complete' }).click()
  const alert = b.locator('.conflict')
  await expect(alert).toContainText('The train is now Aborted')
  await expect(refused).toHaveCount(0)                                                        // a new attempt clears the old message
  await expect(b.locator('main')).toContainText('Aborted · Target')
  await expect(form).toHaveCount(0)
  await expect(alert.getByRole('button', { name: 'Complete anyway' })).toHaveCount(0)
  await alert.getByRole('button', { name: 'Dismiss' }).click()
  await expect(b.locator('.conflict')).toHaveCount(0)
  expect((await train(a, id)).status).toBe('Aborted')
})
