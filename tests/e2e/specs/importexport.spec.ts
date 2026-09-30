import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-48/49: the Imports & exports screen. A file with one bad row shows row + column and cannot be committed; a good file commits; the export list serves CSV and XLSX.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}

const csv = (rows: string[][]) => Buffer.from(rows.map(r => r.join(',')).join('\r\n') + '\r\n')

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('a bad row shows its row and column and blocks the commit; a good file commits; every grid has CSV and XLSX', async ({ page }) => {
      await signIn(page, `importexport-${scheme}@example.com`, 'RTE')
      await page.locator('nav[aria-label=Primary] a', { hasText: 'Imports & exports' }).click()
      await expect(page).toHaveURL(/\/importexport$/)
      await expect(page.locator('h1')).toHaveText('Imports & exports')

      // days nobody else uses: the demo database is shared by every spec
      const year = 3000 + Math.floor(Math.random() * 5000)
      const day = (n: number) => `${year}-01-${String(n).padStart(2, '0')}`
      const stamp = `${scheme}-${Date.now()}`

      // choose the kind, then a file whose second row is not a date
      await page.locator('[role=group][aria-label="Import kind"] button', { hasText: /^Holidays$/ }).click()
      await expect(page.locator('[role=group][aria-label="Import kind"] button', { hasText: /^Holidays$/ })).toHaveAttribute('aria-pressed', 'true')
      await page.locator('input[type=file]').setInputFiles({ name: 'holidays-bad.csv', mimeType: 'text/csv', buffer: csv([['Day', 'Name'], [day(1), `Good ${stamp}`], ['not-a-date', 'Bad row']]) })

      const errors = page.locator('section[aria-label=Errors]')
      await expect(errors).toBeVisible()
      await expect(errors.locator('tbody tr')).toHaveCount(1)
      await expect(errors.locator('tbody tr').first().locator('td').nth(0)).toHaveText('2')          // the row
      await expect(errors.locator('tbody tr').first().locator('td').nth(1)).toHaveText('Day')        // the column
      await expect(errors.locator('tbody tr').first()).toContainText('yyyy-MM-dd')
      await expect(page.locator('.import-bar .counts')).toContainText('1 error')
      await expect(page.locator('.import-bar .counts')).toContainText('1 new')

      // the commit is off, with its reason in words
      const commit = page.getByRole('button', { name: /^Commit/ })
      await expect(commit).toBeDisabled()
      await expect(page.locator('#commit-why')).toContainText('Commit is off while the file has errors')
      await expect(page.locator('section[aria-label=Preview] tbody tr')).toHaveCount(2)
      await expect(page.locator('section[aria-label=Preview] tbody tr').nth(1)).toContainText('error')   // glyph plus word

      // no checkboxes, switches, or modals; mode is a pair of text buttons with aria-pressed
      await expect(page.locator('input[type=checkbox], [role=switch], [role=dialog]')).toHaveCount(0)
      const mode = page.locator('[role=group][aria-label="Import mode"]')
      await expect(mode.locator('button', { hasText: 'Upsert' })).toHaveAttribute('aria-pressed', 'true')
      await mode.locator('button', { hasText: 'Append' }).click()
      await expect(mode.locator('button', { hasText: 'Append' })).toHaveAttribute('aria-pressed', 'true')

      await scan(page, `imports with an error preview (${scheme})`)

      // nothing was written
      const before = await page.evaluate(async d => (await (await fetch('/api/v1/holidays')).json() as { day: string }[]).some(h => h.day === d), day(1))
      expect(before).toBe(false)

      // a good file: two new rows, commit them
      await page.locator('input[type=file]').setInputFiles({ name: 'holidays-good.csv', mimeType: 'text/csv', buffer: csv([['Day', 'Name'], [day(1), `Good ${stamp}`], [day(2), `Also good ${stamp}`]]) })
      await expect(page.locator('.import-bar .counts')).toContainText('2 new')
      await expect(page.locator('.import-bar .counts')).toContainText('0 errors')
      await expect(page.locator('section[aria-label=Errors]')).toHaveCount(0)
      const go = page.getByRole('button', { name: 'Commit 2 rows' })
      await expect(go).toBeEnabled()
      await go.click()
      await expect(page.locator('p.ok', { hasText: 'Committed Holidays: 2 new' })).toBeVisible()
      await expect(page.getByTestId('announcer')).toContainText('Committed Holidays: 2 new')   // spoken through the app's one live region

      const names = await page.evaluate(async () => (await (await fetch('/api/v1/holidays')).json() as { day: string; name: string }[]).map(h => `${h.day} ${h.name}`))
      expect(names).toContain(`${day(1)} Good ${stamp}`)
      expect(names).toContain(`${day(2)} Also good ${stamp}`)

      // history shows it, with words beside the glyph
      const history = page.locator('section[aria-label="Recent imports"]')
      await expect(history.locator('tbody tr').first()).toContainText('Committed')
      await expect(history.locator('tbody tr').first()).toContainText('holidays-good.csv')

      // the same rows again in Append mode are errors: the keys exist now
      await page.locator('input[type=file]').setInputFiles({ name: 'holidays-again.csv', mimeType: 'text/csv', buffer: csv([['Day', 'Name'], [day(1), `Good ${stamp}`]]) })
      await expect(page.locator('section[aria-label=Errors] tbody tr').first()).toContainText('already exists')

      // exports: every grid has CSV and XLSX links, and they serve real files
      const links = page.locator('aside[aria-label=Exports] table.export-grids tbody tr')
      expect(await links.count()).toBeGreaterThanOrEqual(15)
      await expect(page.locator('aside[aria-label=Exports] a[aria-label="Users as CSV"]')).toHaveAttribute('href', '/api/v1/exports/users.csv')
      await expect(page.locator('aside[aria-label=Exports] a[aria-label="Users as XLSX"]')).toHaveAttribute('href', '/api/v1/exports/users.xlsx')
      const csvRes = await page.request.get('/api/v1/exports/holidays.csv')
      expect(csvRes.status()).toBe(200)
      expect(csvRes.headers()['content-disposition']).toContain('attachment')
      expect((await csvRes.text()).replace(/^﻿/, '')).toContain('Day,Name,#Version')
      const xlsxRes = await page.request.get('/api/v1/exports/holidays.xlsx')
      expect(xlsxRes.status()).toBe(200)
      expect((await xlsxRes.body()).subarray(0, 2).toString()).toBe('PK')

      await scan(page, `imports and exports with history and the export list (${scheme})`)
    })

    test('a viewer sees the export list but no import controls', async ({ page }) => {
      await signIn(page, `importexport-viewer-${scheme}@example.com`, 'Viewer')
      await page.locator('nav[aria-label=Primary] a', { hasText: 'Imports & exports' }).click()
      await expect(page.locator('h1')).toHaveText('Imports & exports')
      await expect(page.locator('[role=group][aria-label="Import kind"]')).toHaveCount(0)
      await expect(page.locator('aside[aria-label=Exports] a[aria-label="Trains as CSV"]')).toBeVisible()
      await expect(page.locator('aside[aria-label=Exports] a[aria-label="Audit log as CSV"]')).toHaveCount(0)   // AuditRead only
      await scan(page, `imports and exports as a viewer (${scheme})`)
    })
  })
}
