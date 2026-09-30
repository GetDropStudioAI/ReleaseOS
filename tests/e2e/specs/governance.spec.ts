import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import { signIn, trainByTitle } from './support'

// REOS-33: the change record and the Go/No-Go record, entirely inline (no modals). Decisions are immutable.
test('a Release Manager records Go with conditions inline; an RTE cannot; the change record saves', async ({ page }) => {
  await signIn(page, 'rm@example.com', 'ReleaseManager')
  const t = await trainByTitle(page, 'Reporting')
  await page.goto(`/trains/${t.id}`)

  const go = page.locator('section[aria-label="Go/No-Go"]')
  await expect(go).toContainText('No Go/No-Go recorded yet')
  await page.locator('.header-line button:text-is("Record Go/No-Go")').click()
  await go.locator('button', { hasText: 'Go with conditions' }).click()
  await go.locator('label:has-text("Condition") input').fill('Patch the payment gateway')
  await go.locator('button:text-is("Record decision")').click()
  await expect(go.locator('[role=alert]')).toContainText('expiry')                              // an empty expiry is refused, nothing recorded
  await go.locator('label:has-text("Expires") input').fill('2035-01-01T09:00')
  await go.locator('button:text-is("Record decision")').click()
  await expect(go).toContainText('Go with conditions')
  await expect(go.locator('tr', { hasText: 'Patch the payment gateway' })).toContainText('○ open')

  await go.locator('button:text-is("Close")').click()
  await expect(go.locator('tr', { hasText: 'Patch the payment gateway' })).toContainText('✓ closed')

  // the change record
  const cr = page.locator('section[aria-label="Change record"]')
  await cr.locator('button:text-is("Edit")').click()
  await cr.locator('label:has-text("Justification") textarea').fill('Quarter-close reporting fix')
  await cr.locator('button:text-is("Save change record")').click()
  await expect(cr).toContainText('Quarter-close reporting fix')
  await cr.locator('label:has-text("Item") input').first().fill('Reporting DB')
  await cr.locator('button:text-is("Add item")').click()
  await expect(cr.locator('tr', { hasText: 'Reporting DB' })).toBeVisible()

  const results = await new AxeBuilder({ page }).analyze()
  expect(results.violations.map(v => `${v.id}: ${v.nodes[0]?.html}`)).toEqual([])
})

test('an RTE sees the Go/No-Go but has no button to record it', async ({ page }) => {
  await signIn(page, 'rte2@example.com', 'RTE')
  const t = await trainByTitle(page, 'Reporting')
  await page.goto(`/trains/${t.id}`)
  await expect(page.locator('section[aria-label="Go/No-Go"]')).toBeVisible()
  await expect(page.locator('button:text-is("Record Go/No-Go")')).toHaveCount(0)
})
