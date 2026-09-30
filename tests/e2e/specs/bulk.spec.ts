import { expect, test } from '@playwright/test'
import { signIn, trainByTitle } from './support'

// REOS-28: paste tasks, see errors with line numbers and closest matches, fix, commit; the pasted text survives a closed tab.
test('bulk paste: errors block commit, a fixed paste commits, tasks appear in the checklist', async ({ page }) => {
  await signIn(page, 'bulk@example.com')
  const t = await trainByTitle(page, 'Reporting')
  await page.goto(`/trains/${t.id}`)
  const checklist = page.locator('section[aria-label=Checklist]')
  await checklist.locator('button:has-text("Paste tasks")').click()
  const drawer = page.locator('.inspector')
  await expect(drawer.locator('h2')).toHaveText('Paste tasks')

  await drawer.locator('#bulk-text').fill('# Code Freeze\n- Announce the freeze @demo-rte@example.com\n- Wrong owner and product @nobody@example.com [Payments Ap]\nthis line is not a task')
  await drawer.locator('button:text-is("Parse")').click()
  const preview = drawer.locator('section[aria-label=Preview]')
  await expect(preview).toContainText('3 errors')
  await expect(preview).toContainText('line 3')
  await expect(preview).toContainText('line 4')
  await expect(preview).toContainText('Did you mean: Payments API')                              // closest match
  await expect(preview.getByRole('button', { name: 'Use Payments API on line 3' })).toBeVisible()  // and it is a one-click fix
  await expect(drawer.locator('button:has-text("Commit")')).toBeDisabled()                        // any error blocks commit
  await expect(checklist).not.toContainText('Announce the freeze')                                 // and nothing was added

  await drawer.locator('#bulk-text').fill('# Code Freeze\n- Announce the freeze @demo-rte@example.com\n- Freeze the wiki [Payments API]')
  await expect(preview).toHaveCount(0)                                                             // editing makes the old preview go away
  await drawer.locator('button:text-is("Parse")').click()
  await expect(preview).toContainText('2 tasks ready')
  await expect(preview).toContainText('1 warning')                                                 // the second line has no @owner: gate owner fallback
  await drawer.locator('button:has-text("Commit 2 tasks")').click()

  await expect(checklist).toContainText('Announce the freeze')                                     // committed: drawer closed, checklist refreshed
  await expect(checklist).toContainText('Freeze the wiki')
  await expect(drawer.locator('h2')).toHaveCount(0)                                              // the drawer is gone
})

test('the pasted text is kept when the tab is closed, until it is discarded', async ({ browser }) => {
  const ctx = await browser.newContext()
  const a = await ctx.newPage()
  await signIn(a, 'bulk2@example.com')
  const t = await trainByTitle(a, 'Payments platform')
  await a.goto(`/trains/${t.id}`)
  await a.locator('section[aria-label=Checklist] button:has-text("Paste tasks")').click()
  await a.locator('#bulk-text').fill('# Code Freeze\n- half pasted, not parsed yet')
  await a.waitForResponse(r => r.url().includes('/api/v1/me/session/') && r.request().method() === 'PUT' && r.ok())
  await a.close()

  const b = await ctx.newPage()
  await b.goto('/')
  await expect(b.locator('.inspector h2')).toHaveText('Paste tasks')                               // the drawer is open again
  await expect(b.locator('#bulk-text')).toHaveValue('# Code Freeze\n- half pasted, not parsed yet')
  await b.locator('button:text-is("Discard")').click()
  await expect(b.locator('.inspector h2')).toHaveCount(0)
  await ctx.close()
})
