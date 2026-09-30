import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import { signIn } from './support'

// REOS-38: governed train templates, entirely inline (no modals). Draft -> Approved -> Retired.
test('a Release Manager drafts, approves and retires a template; a Viewer sees it read only', async ({ page }) => {
  const name = `E2E template ${Date.now()}`
  await signIn(page, 'templates-rm@example.com', 'ReleaseManager')
  await page.click('nav[aria-label=Primary] a:has-text("Templates")')
  await expect(page.locator('h1')).toHaveText('Train templates')
  await expect(page.locator('table.grid tr', { hasText: 'Standard release' })).toContainText('○ Draft')

  await page.locator('button:text-is("New template")').click()
  await page.locator('label:has-text("Name") input').fill(name)
  await page.locator('input[aria-label="Gate 1 name"]').fill('Code Freeze')
  await page.locator('button:text-is("Add step")').click()
  await page.locator('input[aria-label="Step 1 code"]').fill('PRE-1')
  await page.locator('input[aria-label="Step 1 title"]').fill('Health check')
  await page.locator('button:text-is("Create draft")').click()

  const row = page.locator('table.grid tr', { hasText: name })
  await expect(row).toContainText('○ Draft')
  await expect(page.locator('h2', { hasText: name })).toBeVisible()
  await expect(page.locator('table.grid tr', { hasText: 'Code Freeze' })).toContainText('Standard')
  await expect(page.locator('table.grid tr', { hasText: 'PRE-1' })).toContainText('Health check')

  await page.locator('button:text-is("Edit")').click()
  await page.locator('input[aria-label="Gate 1 name"]').fill('Code Freeze v2')
  await page.locator('button:text-is("Save draft")').click()
  await expect(page.locator('table.grid tr', { hasText: 'Code Freeze v2' })).toBeVisible()

  await page.locator('button:text-is("Approve")').click()
  await expect(row).toContainText('✓ Approved')
  await expect(row).toContainText('templates-rm')
  await expect(page.locator('button:text-is("Edit")')).toHaveCount(0)           // approved templates are immutable

  await page.locator('button:text-is("Retire")').click()                        // an inline confirm, not a modal
  await page.locator('button:text-is("Retire template")').click()
  await expect(row).toContainText('✗ Retired')
  await expect(page.locator('button:text-is("Approve")')).toHaveCount(0)

  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()
  expect(r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')).toEqual([])
})

test('an RTE can draft but not approve; a Viewer has no actions', async ({ page }) => {
  await signIn(page, 'templates-rte@example.com', 'RTE')
  await page.click('nav[aria-label=Primary] a:has-text("Templates")')
  await page.getByRole('button', { name: 'Standard release', exact: true }).click()
  await expect(page.locator('button:text-is("Edit")')).toBeVisible()
  await expect(page.locator('button:text-is("Approve")')).toHaveCount(0)

  await page.goto('/')
  await page.click('button:text-is("Sign out")')
  await expect(page.locator('input[type=email]')).toBeVisible()   // the logout request has finished; navigating earlier can abort it and keep the session
  await signIn(page, 'templates-viewer@example.com', 'Viewer')
  await page.click('nav[aria-label=Primary] a:has-text("Templates")')
  await expect(page.locator('button:text-is("New template")')).toHaveCount(0)
  await page.getByRole('button', { name: 'Standard release', exact: true }).click()
  await expect(page.locator('button:text-is("Edit")')).toHaveCount(0)
  await expect(page.locator('button:text-is("Approve")')).toHaveCount(0)
})
