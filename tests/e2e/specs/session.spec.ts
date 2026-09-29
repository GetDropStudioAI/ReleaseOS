import { expect, test } from '@playwright/test'
import { signIn, trainByTitle } from './support'

// REOS-26: closing a tab mid-draft loses nothing; a deep link reopens the Inspector on that gate.
test('closing a tab mid-draft and reopening keeps the draft, the train and the filter', async ({ browser }) => {
  const ctx = await browser.newContext()
  const a = await ctx.newPage()
  await signIn(a, 'session-user@example.com')
  await a.click('.stream-row:has-text("Reporting")')
  await expect(a.locator('h1')).toContainText('Reporting')

  await a.fill('#flt', 'Rep')
  await a.click('.section-head button:has-text("Add task")')
  await a.fill('input[placeholder="What has to happen"]', 'Half-typed task that must survive')

  // the 1 s debounce saves it; wait for the PUT to land rather than sleeping
  await a.waitForResponse(r => r.url().includes('/api/v1/me/session/') && r.request().method() === 'PUT' && r.ok())
  await a.close()                                                        // the tab is gone (its sessionStorage with it)

  const b = await ctx.newPage()                                          // a new tab starts from the user's most recent state
  await b.goto('/')
  await expect(b.locator('h1')).toContainText('Reporting')               // the train comes back with no click
  await expect(b.locator('input[placeholder="What has to happen"]')).toHaveValue('Half-typed task that must survive')
  await expect(b.locator('#flt')).toHaveValue('Rep')
  await ctx.close()
})

test('a deep link reopens the Inspector on that gate, and survives a refresh', async ({ page }) => {
  await signIn(page, 'deeplink-user@example.com')
  const t = await trainByTitle(page, 'Card portal')
  const gate = t.gates.find(g => g.name === 'Compliance Sign-off')!

  await page.goto(`/trains/${t.id}/gates/${gate.id}`)
  await expect(page.locator('.inspector h2')).toHaveText('Compliance Sign-off')
  await page.reload()
  await expect(page.locator('.inspector h2')).toHaveText('Compliance Sign-off')

  // selecting another gate changes the URL, and Back returns to the first
  await page.click('svg.timeline g.tl-gate:has-text("CAB Approval")')
  await expect(page.locator('.inspector h2')).toHaveText('CAB Approval')
  expect(new URL(page.url()).pathname).toContain('/gates/')
  await page.goBack()
  await expect(page.locator('.inspector h2')).toHaveText('Compliance Sign-off')
})

test('a deep link to something that does not exist says so instead of hanging', async ({ page }) => {
  await signIn(page, 'deeplink2@example.com')
  await page.goto('/trains/does-not-exist/gates/nope')
  await expect(page.getByRole('alert').first()).toBeVisible()
})
