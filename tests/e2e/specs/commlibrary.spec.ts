import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-43/44: comm library (tokens highlighted, hydrated preview with "data as of"), per-train T-minus schedule, roles. Everything inline, no modals.

const openLibrary = async (page: Page) => {
  await page.click('nav[aria-label=Primary] a:has-text("Comm library")')
  await expect(page.locator('main h1')).toHaveText('Comm library')
  await expect(page.locator('table.os-table tr', { hasText: 'T-1 Go/No-Go outcome' })).toBeVisible()   // the seeded default library
}
const trainSelect = (page: Page) => page.locator('label:has-text("Preview and schedule for train") select')
const pickTrain = async (page: Page, title: string) => {
  const sel = trainSelect(page)
  await sel.selectOption(await sel.locator('option', { hasText: title }).getAttribute('value') as string)
}

test('a Release Manager writes a template: an unknown token is flagged and blocks, a fixed one previews with live data', async ({ page }) => {
  const name = `E2E notice ${Date.now()}`
  await signIn(page, 'comm-rm@example.com', 'ReleaseManager')
  await openLibrary(page)

  // a seeded template previews for the chosen train with its "data as of"
  await pickTrain(page, 'R26.13')
  await page.locator('table.os-table tr', { hasText: 'T-1 Go/No-Go outcome' }).click()   // a row selects on click
  await expect(page.locator('.mg-summary', { hasText: 'Template' })).toContainText('all known ✓')
  const preview = page.locator('[aria-label="Hydrated message"]')
  await expect(preview).toContainText('R26.13 Reporting')
  await expect(preview).toContainText('Not yet recorded')                 // no Go/No-Go recorded yet
  await expect(preview).toContainText('None')                             // empty lists print None
  await expect(page.locator('.mg-summary', { hasText: 'Preview' })).toContainText(/data as of \d\d:\d\d UTC · train v\d+/)

  // new template with a typo
  await page.getByRole('button', { name: 'New template', exact: true }).click()
  await page.locator('label:has-text("Name") input').fill(name)
  await page.locator('label:has-text("Type") input').fill('Custom')
  await page.locator('label:has-text("Subject") input').fill('[{ReleaseTitle}] update')
  await page.locator('textarea.mg-body').fill('Hello {ReleaseTitel}, status {Status}.')
  const check = page.locator('.mg-summary', { hasText: 'Template' })
  await expect(check).toContainText('✗ 1 problem')
  await expect(page.locator('li.mg-problem', { hasText: 'Did you mean {ReleaseTitle}' })).toBeVisible()
  await expect(page.locator('.mg-tkbad', { hasText: '{ReleaseTitel}' })).toBeVisible()
  await expect(page.locator('.mg-tk', { hasText: '{Status}' }).first()).toBeVisible()
  await expect(page.locator('p.mg-problem', { hasText: 'Cannot be sent' })).toBeVisible()

  // fixed
  await page.locator('textarea.mg-body').fill('Hello {ReleaseTitle}, status {Status}.')
  await expect(check).toContainText('all known ✓')
  await expect(preview).toContainText('Hello R26.13 Reporting, status Planning.')
  await page.getByRole('button', { name: 'Create template', exact: true }).click()
  const row = page.locator('table.os-table tr', { hasText: name })
  await expect(row).toContainText('All known')
  await expect(row).toContainText('Custom')

  // edit and save (If-Match version moves)
  await page.locator('textarea.mg-body').fill('Changed {Status}')
  await page.getByRole('button', { name: 'Save changes', exact: true }).click()
  await expect(row.locator('td').last()).toHaveText('2')

  // copy into the train, edit the train's own copy
  await page.getByRole('button', { name: 'Add to R26.13 Reporting', exact: true }).click()
  const msgs = page.locator('table.os-table tr', { hasText: name })
  await expect(msgs).toHaveCount(2)                                       // the library row and the train's message row
  await expect(page.locator('h2.os-panel-title', { hasText: 'R26.13 Reporting' }).first()).toBeVisible()
  await page.locator('textarea.mg-body').fill('Tailored for this train: {Status}')
  const saved = page.waitForResponse(r => r.request().method() === 'PUT' && r.url().includes('/api/v1/comm-templates/') && r.ok())
  await page.getByRole('button', { name: 'Save changes', exact: true }).click()
  await saved                                                            // the train copy's save has landed (clicking away earlier let it snap back: CI run 74)
  await page.locator('table.os-table tr', { hasText: name }).first().click()   // the library row: its text is unchanged
  await expect(page.locator('textarea.mg-body')).toHaveValue('Changed {Status}')

  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()
  expect(r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')).toEqual([])
})

test('the T-minus schedule is seeded from a train template and a send is recorded against its due time', async ({ page }) => {
  await signIn(page, 'comm-rte@example.com', 'RTE')
  await openLibrary(page)
  await pickTrain(page, 'R26.13')
  const seed = page.getByRole('button', { name: 'Create schedule', exact: true })
  await expect(seed.or(page.locator('table.os-table th', { hasText: 'When' }))).toBeVisible()
  if (await seed.count() > 0) {
    await seed.click()
  }
  const rows = page.locator('table.os-table:has(th:text-is("When")) tbody tr')
  await expect(rows).toHaveCount(6)
  await expect(rows.nth(0)).toContainText('T−7')
  await expect(rows.nth(0)).toContainText('T-7 Readiness notice')
  await expect(rows.last()).toContainText('T+5')
  await expect(page.locator('table.os-table:has(th:text-is("When")) th', { hasText: /^Due \(/ })).toBeVisible()

  // record the first message as sent: a confirmation dialog, then SentAt is shown against DueAt in words
  const first = rows.nth(0)
  if (await first.getByRole('button', { name: 'Mark sent' }).count() > 0) {
    await first.getByRole('button', { name: 'Mark sent' }).click()
    const dialog = page.getByRole('dialog')
    await expect(dialog).toContainText('Record as sent now?')
    await dialog.getByRole('button', { name: 'Record as sent' }).click()
  }
  await expect(first).toContainText(/sent .*on time|sent .*late/i)
  await expect(first.getByRole('button', { name: 'Mark sent' })).toHaveCount(0)     // SentAt is history: it cannot be marked twice
})

test('a Viewer reads the library and previews but has no editing or sending actions', async ({ page }) => {
  await signIn(page, 'comm-viewer@example.com', 'Viewer')
  await openLibrary(page)
  await expect(page.getByRole('button', { name: 'New template', exact: true })).toHaveCount(0)
  await pickTrain(page, 'R26.13')
  await page.locator('table.os-table tr', { hasText: 'T-1 Go/No-Go outcome' }).first().click()
  await expect(page.locator('textarea.mg-body')).toHaveAttribute('readonly', '')
  await expect(page.getByRole('button', { name: 'Save changes', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Add to R26.13 Reporting', exact: true })).toHaveCount(0)
  await expect(page.locator('[aria-label="Hydrated message"]')).toContainText('R26.13 Reporting')   // preview only reads
  await expect(page.getByRole('button', { name: 'Mark sent', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Create schedule', exact: true })).toHaveCount(0)
})
