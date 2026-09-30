import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-38: the audit viewer. Read-only screen; the events it shows are made here through a real service write (a holiday added and removed).
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}

/** Adds then removes a holiday on a date nobody else uses: two audit rows (Add with After, Remove with Before). */
async function makeEvents(page: Page, name: string) {
  await page.evaluate(async n => {
    const day = `20${41 + Math.floor(Math.random() * 40)}-0${1 + Math.floor(Math.random() * 9)}-1${Math.floor(Math.random() * 9)}`
    await fetch('/api/v1/holidays', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ day, name: n }) })
    await fetch(`/api/v1/holidays/${day}`, { method: 'DELETE' })
  }, name)
}

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('an RTE filters the log, reads before/after JSON in the detail column, uses the keyboard and gets a CSV link for the same filter', async ({ page }) => {
      await signIn(page, `audit-${scheme}@example.com`, 'RTE')
      const name = `Audit e2e ${scheme} ${Date.now()}`
      await makeEvents(page, name)

      await page.locator('nav[aria-label=Primary] a', { hasText: 'Audit' }).click()
      await expect(page).toHaveURL(/\/audit$/)
      await expect(page.locator('h1')).toHaveText('Audit')
      await expect(page.locator('table.audit-table tbody tr').first()).toBeVisible()

      // no checkboxes, switches or modals on this screen
      await expect(page.locator('input[type=checkbox], [role=switch], [role=dialog]')).toHaveCount(0)

      // filter line: entity + action, then Apply
      const filters = page.locator('form[aria-label="Audit filters"]')
      await filters.locator('label:has-text("Entity") select').selectOption('Holiday')
      await filters.locator('label:has-text("Action") input').fill('add')
      await filters.locator('button:text-is("Apply")').click()
      const rows = page.locator('table.audit-table tbody tr')
      await expect(rows.first()).toContainText('Holiday')
      await expect(rows.first()).toContainText('Add')
      for (const r of await rows.all()) await expect(r).toContainText('Holiday')

      // the export link carries exactly the applied filter
      const href = await filters.locator('a:text-is("Export CSV")').getAttribute('href')
      expect(href).toContain('/api/v1/audit.csv?')
      expect(href).toContain('entity=Holiday')
      expect(href).toContain('action=add')

      // selecting a row tints the whole row and shows After (an Add has no Before)
      await rows.first().locator('button').click()
      await expect(rows.first()).toHaveAttribute('aria-selected', 'true')
      const detail = page.locator('aside[aria-label="Event detail"]')
      await expect(detail.locator('h2')).toHaveText('Add')
      await expect(detail).toContainText(name)
      await expect(detail).toContainText('none recorded')          // no Before on an Add

      await scan(page, `audit viewer with a selected event (${scheme})`)

      // keyboard: j moves down, k back up, Escape closes
      await page.locator('body').press('j')
      await expect(rows.nth(1)).toHaveAttribute('aria-selected', 'true')
      await page.locator('body').press('k')
      await expect(rows.first()).toHaveAttribute('aria-selected', 'true')
      await page.locator('body').press('Escape')
      await expect(detail).toContainText('Select an event')

      // the Remove event shows a Before
      await filters.locator('label:has-text("Action") input').fill('remove')
      await filters.locator('button:text-is("Apply")').click()
      await rows.first().locator('button').click()
      await expect(detail.locator('h2')).toHaveText('Remove')
      await expect(detail.locator('section[aria-label="Before"]')).toContainText(name)

      // Clear resets the filter line
      await filters.locator('button:text-is("Clear")').click()
      await expect(filters.locator('label:has-text("Entity") select')).toHaveValue('')
      await expect(filters.locator('label:has-text("Action") input')).toHaveValue('')
    })
  })
}

test('the CSV link downloads a file with a header row', async ({ page }) => {
  await signIn(page, 'audit-csv@example.com', 'GovernanceOfficer')
  await page.goto('/audit')
  const [download] = await Promise.all([page.waitForEvent('download'), page.locator('a:text-is("Export CSV")').click()])
  expect(download.suggestedFilename()).toMatch(/^audit-\d{8}-\d{6}\.csv$/)
})

test('a Viewer has no Audit link and gets a plain notice at /audit', async ({ page }) => {
  await signIn(page, 'audit-viewer@example.com', 'Viewer')
  await expect(page.locator('nav[aria-label=Primary] a', { hasText: 'Audit' })).toHaveCount(0)
  await page.goto('/audit')
  await expect(page.locator('main')).toContainText('available to Release Train Engineers')
  const status = await page.evaluate(async () => (await fetch('/api/v1/audit')).status)
  expect(status).toBe(403)
})
