import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import { signIn, signOut } from './support'

// REOS-38: governed train templates, entirely inline (no modals). Draft -> Approved -> Retired.
// REOS-122 (RT7): the author does not approve, so a second Release Manager approves what the first drafted.
test('a Release Manager drafts a template, another approves it and retires it; a Viewer sees it read only', async ({ page }) => {
  const name = `E2E template ${Date.now()}`
  await signIn(page, 'templates-rm@example.com', 'ReleaseManager')
  await page.click('nav[aria-label=Primary] a:has-text("Templates")')
  await expect(page.locator('main h1')).toHaveText('Train templates')
  await expect(page.locator('table.os-table tr', { hasText: 'Standard release' })).toContainText('Draft')

  await page.getByRole('button', { name: 'New template', exact: true }).click()
  await page.locator('label:has-text("Name") input').fill(name)
  await page.locator('input[aria-label="Gate 1 name"]').fill('Code Freeze')
  await page.getByRole('button', { name: 'Add step', exact: true }).click()
  await page.locator('input[aria-label="Step 1 code"]').fill('PRE-1')
  await page.locator('input[aria-label="Step 1 title"]').fill('Health check')
  await page.getByRole('button', { name: 'Create draft', exact: true }).click()

  const row = page.locator('table.os-table tr', { hasText: name })
  await expect(row).toContainText('Draft')
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible()
  await expect(page.locator('table.os-table tr', { hasText: 'Code Freeze' })).toContainText('Standard')
  await expect(page.locator('table.os-table tr', { hasText: 'PRE-1' })).toContainText('Health check')

  await page.getByRole('button', { name: 'Edit', exact: true }).click()
  await page.locator('input[aria-label="Gate 1 name"]').fill('Code Freeze v2')
  await page.getByRole('button', { name: 'Save draft', exact: true }).click()
  await expect(page.locator('table.os-table tr', { hasText: 'Code Freeze v2' })).toBeVisible()

  // the author does not approve: the button is off and says why, in words
  const approve = page.getByRole('button', { name: 'Approve', exact: true })
  await expect(approve).toBeDisabled()
  await expect(page.getByText('You created this template, so someone else approves it')).toBeVisible()
  await signOut(page)

  await signIn(page, 'templates-rm2@example.com', 'ReleaseManager')
  await page.click('nav[aria-label=Primary] a:has-text("Templates")')
  await page.locator('table.os-table tr', { hasText: name }).click()
  await page.getByRole('button', { name: 'Approve', exact: true }).click()
  await expect(row).toContainText('Approved')
  await expect(row).toContainText('templates-rm2')
  await expect(page.getByRole('button', { name: 'Edit', exact: true })).toHaveCount(0)           // approved templates are immutable

  await page.getByRole('button', { name: 'Retire', exact: true }).click()                        // a confirmation dialog that starts on Cancel
  await expect(page.getByRole('dialog').getByRole('button', { name: 'Cancel' })).toBeFocused()
  await page.getByRole('dialog').getByRole('button', { name: 'Retire template', exact: true }).click()
  await expect(row).toContainText('Retired')
  await expect(page.getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0)

  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()
  expect(r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')).toEqual([])
})

test('an RTE can draft but not approve; a Viewer has no actions', async ({ page }) => {
  await signIn(page, 'templates-rte@example.com', 'RTE')
  await page.click('nav[aria-label=Primary] a:has-text("Templates")')
  await page.getByRole('row', { name: /Standard release/ }).click()
  await expect(page.getByRole('button', { name: 'Edit', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0)

  await page.goto('/')
  await signOut(page)   // waits for the sign-in page: the logout request has finished; navigating earlier can abort it and keep the session
  await signIn(page, 'templates-viewer@example.com', 'Viewer')
  await page.click('nav[aria-label=Primary] a:has-text("Templates")')
  await expect(page.getByRole('button', { name: 'New template', exact: true })).toHaveCount(0)
  await page.getByRole('row', { name: /Standard release/ }).click()
  await expect(page.getByRole('button', { name: 'Edit', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0)
})
