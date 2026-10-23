import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { primaryNav, signIn } from './support'

// REOS-38 / REOS-101: Audit history. Read-only screen; the events it shows are made here through a real service write (a holiday added and removed).
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

const rowsOf = (page: Page) => page.locator('table.au-table tbody tr[data-event]')

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('an RTE filters the log, reads the change in the details panel, uses the keyboard, copies the record and exports the same filter', async ({ page, context }) => {
      await context.grantPermissions(['clipboard-read', 'clipboard-write'])
      await signIn(page, `audit-${scheme}@example.com`, 'RTE')
      const name = `Audit e2e ${scheme} ${Date.now()}`
      await makeEvents(page, name)
      await makeEvents(page, `${name} (2)`)     // two Add rows, so Up and Down have somewhere to go on a fresh database

      await primaryNav(page).getByRole('link', { name: /^Audit history$/ }).click()
      await expect(page).toHaveURL(/\/audit$/)
      await expect(page.locator('main h1')).toHaveText('Audit history')
      await expect(rowsOf(page).first()).toBeVisible()

      // the integrity banner reports what the app really checks, and does not claim a hash chain it does not have
      const banner = page.getByRole('region', { name: 'Audit integrity' })
      await expect(banner).toContainText('Append-only protection verified')
      await expect(banner).toContainText('Not hash-chained or signed yet')
      await expect(banner).toContainText('Retained events')
      await expect(banner).toContainText('Kept indefinitely')
      await expect(banner).toContainText('UTC')

      // filters combine: event type + search; the count and the table follow
      const filters = page.getByRole('form', { name: 'Audit filters' })
      await filters.getByLabel('Event type').selectOption('Holiday')
      await filters.getByRole('searchbox', { name: 'Search event names' }).fill('add')
      await expect(filters.getByRole('status')).toContainText(/\d+ events? match the filters/)
      await expect(rowsOf(page).first()).toContainText('Add holiday')
      for (const r of await rowsOf(page).all()) await expect(r).toContainText('Holiday')
      await expect(page.locator('.au-pager')).toContainText(/Showing 1–\d+ of \d+ events/)

      // no checkboxes or switches on this screen
      await expect(page.locator('input[type=checkbox], [role=switch]')).toHaveCount(0)

      // selecting a row opens the details panel, and the URL carries the event
      await rowsOf(page).first().click()
      const detail = page.getByRole('complementary', { name: 'Event details' })
      await expect(detail.getByRole('heading', { name: 'Add holiday' })).toBeVisible()
      await expect(page).toHaveURL(/\/audit\?event=\d+$/)
      const eventNo = new URL(page.url()).searchParams.get('event')!
      await expect(detail).toContainText(`EVT-${eventNo.padStart(6, '0')}`)
      await expect(detail).toContainText('UTC')
      // the diff names each line in words as well as colour: an Add has only "Added" lines
      const diff = detail.getByRole('group', { name: 'Change' })
      await expect(diff).toContainText(name)
      await expect(diff.locator('.au-line.added').first()).toContainText('Added:')
      await expect(diff.locator('.au-line.removed')).toHaveCount(0)
      await expect(detail).toContainText('Not signed')               // the signature line says what is true
      await scan(page, `audit history with an event open (${scheme})`)

      // Copy event record: complete, machine-readable, confirmed
      await detail.getByRole('button', { name: 'Copy event record' }).click()
      await expect(detail.getByRole('status')).toContainText('Copied')
      const record = JSON.parse(await page.evaluate(() => navigator.clipboard.readText()))
      expect(record).toMatchObject({ id: Number(eventNo), entity: { type: 'Holiday' }, action: 'Add', outcome: 'Recorded', before: null })
      expect(JSON.stringify(record.after)).toContain(name)

      // keyboard: Esc closes the panel and returns focus to the row; Up and Down move the selection; Enter opens it
      await page.keyboard.press('Escape')
      await expect(detail).toHaveCount(0)
      await expect(page).toHaveURL(/\/audit$/)
      await expect(rowsOf(page).first()).toBeFocused()
      await page.keyboard.press('ArrowDown')
      await expect(rowsOf(page).nth(1)).toBeFocused()
      await expect(rowsOf(page).nth(1)).toHaveClass(/selected/)
      await page.keyboard.press('ArrowUp')
      await expect(rowsOf(page).first()).toHaveClass(/selected/)
      await page.keyboard.press('Enter')
      await expect(detail).toBeVisible()
      await expect(detail.getByRole('button', { name: /Close event details/ })).toBeFocused()
      await page.keyboard.press('Escape')
      await expect(detail).toHaveCount(0)

      // a shared link opens the same event, also when it is not on the loaded page
      await page.goto(`/audit?event=${eventNo}`)
      await expect(page.getByRole('complementary', { name: 'Event details' }).getByRole('heading', { name: 'Add holiday' })).toBeVisible()
      await page.getByRole('button', { name: /Close event details/ }).click()
      await expect(page).toHaveURL(/\/audit$/)
      await page.goto('/audit?event=99999999')
      await expect(page.getByRole('complementary', { name: 'Event details' })).toContainText('does not exist in this workspace')

      // Export audit log carries exactly the applied filters
      await page.goto('/audit')
      await filters.getByLabel('Event type').selectOption('Holiday')
      await filters.getByRole('searchbox', { name: 'Search event names' }).fill('remove')
      await expect(rowsOf(page).first()).toContainText('Remove holiday')
      const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Export audit log' }).click()])
      expect(download.suggestedFilename()).toMatch(/^audit-\d{8}-\d{6}\.csv$/)
      expect(download.url()).toContain('entity=Holiday')
      expect(download.url()).toContain('action=remove')
      // the Remove event has a Before and shows it as removed lines
      await rowsOf(page).first().click()
      await expect(detail.locator('.au-line.removed').first()).toContainText('Removed:')

      // Clear filters resets the bar
      await filters.getByRole('button', { name: 'Clear filters' }).click()
      await expect(filters.getByLabel('Event type')).toHaveValue('')
      await expect(filters.getByRole('searchbox', { name: 'Search event names' })).toHaveValue('')
    })

    test('the integrity banner shows a clear failed state when the protection cannot be verified, and when the check itself fails', async ({ page }) => {
      await signIn(page, `audit-bad-${scheme}@example.com`, 'GovernanceOfficer')
      await page.route('**/api/v1/audit/integrity', r => r.fulfill({ json: { appendOnly: false, missingTriggers: ['trg_Audit_NoDelete'], hashChained: false, retainedEvents: 40, oldestAt: null, newestAt: null, checkedAt: '2026-10-23T14:32:08Z' } }))
      await page.goto('/audit')
      const banner = page.getByRole('region', { name: 'Audit integrity' })
      await expect(banner).toContainText('Audit log protection could not be verified')
      await expect(banner).toContainText('trg_Audit_NoDelete')
      await expect(banner).not.toContainText('verified.')
      await scan(page, `audit history with a failed integrity check (${scheme})`)

      await page.unroute('**/api/v1/audit/integrity')
      await page.route('**/api/v1/audit/integrity', r => r.fulfill({ status: 500, json: { message: 'check failed' } }))
      await page.goto('/audit')
      await expect(page.getByText('The audit integrity check could not be loaded')).toBeVisible()
    })
  })
}

test('the table pages with Previous and Next, and the count and range follow', async ({ page }) => {
  await signIn(page, 'audit-pages@example.com', 'RTE')
  await page.evaluate(async () => {           // 28 events (14 add + remove pairs) on days no one else uses, so there are at least two pages of Holiday events
    for (let d = 1; d <= 14; d++) {
      const day = `2099-12-${String(d).padStart(2, '0')}`
      await fetch('/api/v1/holidays', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ day, name: `Paging ${d}` }) })
      await fetch(`/api/v1/holidays/${day}`, { method: 'DELETE' })
    }
  })
  await page.goto('/audit')
  const filters = page.getByRole('form', { name: 'Audit filters' })
  await filters.getByLabel('Event type').selectOption('Holiday')
  const pager = page.locator('.au-pager')
  await expect(pager).toContainText(/Showing 1–25 of \d+ events/)
  await expect(rowsOf(page)).toHaveCount(25)
  await expect(pager.getByRole('button', { name: 'Previous' })).toBeDisabled()
  await pager.getByRole('button', { name: 'Next' }).click()
  await expect(pager).toContainText(/Showing 26–\d+ of \d+ events/)
  await pager.getByRole('button', { name: 'Previous' }).click()
  await expect(pager).toContainText(/Showing 1–25 of/)
  await pager.getByRole('button', { name: 'Next' }).click()
  await filters.getByLabel('From (UTC)').fill('2000-01-01')                // a filter change starts again at page one
  await expect(pager).toContainText(/Showing 1–25 of/)
})

test('a Viewer has no Audit link and gets a plain notice at /audit', async ({ page }) => {
  await signIn(page, 'audit-viewer@example.com', 'Viewer')
  await expect(primaryNav(page).getByRole('link', { name: /^Audit history$/ })).toHaveCount(0)
  await page.goto('/audit')
  await expect(page.locator('main')).toContainText('You cannot read the audit log')
  await expect(page.locator('main')).toContainText('limited to Release Train Engineers')
  for (const url of ['/api/v1/audit', '/api/v1/audit/integrity', '/api/v1/audit/1', '/api/v1/audit.csv']) {
    expect(await page.evaluate(async u => (await fetch(u)).status, url), url).toBe(403)
  }
})
