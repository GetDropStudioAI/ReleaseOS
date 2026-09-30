import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-43/44: comm library (tokens highlighted, hydrated preview with "data as of"), per-train T-minus schedule, roles. Everything inline, no modals.

const openLibrary = async (page: Page) => {
  await page.click('nav[aria-label=Primary] a:has-text("Comm library")')
  await expect(page.locator('h1')).toHaveText('Comm library')
  await expect(page.locator('table.grid tr', { hasText: 'T-1 Go/No-Go outcome' })).toBeVisible()   // the seeded default library
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
  await page.locator('table.grid tr', { hasText: 'T-1 Go/No-Go outcome' }).click()
  await expect(page.locator('.token-summary', { hasText: 'Template' })).toContainText('all known ✓')
  const preview = page.locator('[aria-label="Hydrated message"]')
  await expect(preview).toContainText('R26.13 Reporting')
  await expect(preview).toContainText('Not yet recorded')                 // no Go/No-Go recorded yet
  await expect(preview).toContainText('None')                             // empty lists print None
  await expect(page.locator('.token-summary', { hasText: 'Preview' })).toContainText(/data as of \d\d:\d\d · train v\d+/)

  // new template with a typo
  await page.locator('button:text-is("New template")').click()
  await page.locator('label:has-text("Name") input').fill(name)
  await page.locator('label:has-text("Type") input').fill('Custom')
  await page.locator('label:has-text("Subject") input').fill('[{ReleaseTitle}] update')
  await page.locator('textarea.bulk-text').fill('Hello {ReleaseTitel}, status {Status}.')
  const check = page.locator('.token-summary', { hasText: 'Template' })
  await expect(check).toContainText('✗ 1 problem')
  await expect(page.locator('li.bad', { hasText: 'Did you mean {ReleaseTitle}' })).toBeVisible()
  await expect(page.locator('.tkbad', { hasText: '{ReleaseTitel}' })).toBeVisible()
  await expect(page.locator('.tk', { hasText: '{Status}' }).first()).toBeVisible()
  await expect(page.locator('p.bad', { hasText: 'Cannot be sent' })).toBeVisible()

  // fixed
  await page.locator('textarea.bulk-text').fill('Hello {ReleaseTitle}, status {Status}.')
  await expect(check).toContainText('all known ✓')
  await expect(preview).toContainText('Hello R26.13 Reporting, status Planning.')
  await page.locator('button:text-is("Create template")').click()
  const row = page.locator('table.grid tr', { hasText: name })
  await expect(row).toContainText('✓ all known')
  await expect(row).toContainText('Custom')

  // edit and save (If-Match version moves)
  await page.locator('textarea.bulk-text').fill('Changed {Status}')
  await page.locator('button:text-is("Save changes")').click()
  await expect(row.locator('td.n').last()).toHaveText('2')

  // copy into the train, edit the train's own copy
  await page.locator('button:text-is("Add to R26.13 Reporting")').click()
  const msgs = page.locator('table.grid tr', { hasText: name })
  await expect(msgs).toHaveCount(2)                                       // the library row and the train's message row
  await expect(page.locator('h2.cap', { hasText: 'R26.13 Reporting' }).first()).toBeVisible()
  await page.locator('textarea.bulk-text').fill('Tailored for this train: {Status}')
  await page.locator('button:text-is("Save changes")').click()
  await page.locator('table.grid tr', { hasText: name }).first().click()                          // the library row: its text is unchanged
  await expect(page.locator('textarea.bulk-text')).toHaveValue('Changed {Status}')

  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()
  expect(r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')).toEqual([])
})

test('the T-minus schedule is seeded from a train template and a send is recorded against its due time', async ({ page }) => {
  await signIn(page, 'comm-rte@example.com', 'RTE')
  await openLibrary(page)
  await pickTrain(page, 'R26.13')
  const seed = page.locator('button:text-is("Create schedule")')
  await expect(seed.or(page.locator('table.grid th', { hasText: 'When' }))).toBeVisible()
  if (await seed.count() > 0) {
    await seed.click()
  }
  const rows = page.locator('table.grid:has(th:text-is("When")) tbody tr')
  await expect(rows).toHaveCount(6)
  await expect(rows.nth(0)).toContainText('T−7')
  await expect(rows.nth(0)).toContainText('T-7 Readiness notice')
  await expect(rows.last()).toContainText('T+5')
  await expect(page.locator('table.grid:has(th:text-is("When")) th', { hasText: /^Due \(/ })).toBeVisible()

  // record the first message as sent: an inline confirmation, then SentAt is shown against DueAt in words
  const first = rows.nth(0)
  if (await first.locator('button:text-is("Mark sent")').count() > 0) {
    await first.locator('button:text-is("Mark sent")').click()
    await expect(first).toContainText('Record as sent now?')
    await first.locator('button:text-is("Confirm")').click()
  }
  await expect(first).toContainText(/✓ sent .*on time|▲ sent .*late/)
  await expect(first.locator('button:text-is("Mark sent")')).toHaveCount(0)     // SentAt is history: it cannot be marked twice
})

test('a Viewer reads the library and previews but has no editing or sending actions', async ({ page }) => {
  await signIn(page, 'comm-viewer@example.com', 'Viewer')
  await openLibrary(page)
  await expect(page.locator('button:text-is("New template")')).toHaveCount(0)
  await pickTrain(page, 'R26.13')
  await page.locator('table.grid tr', { hasText: 'T-1 Go/No-Go outcome' }).click()
  await expect(page.locator('textarea.bulk-text')).toHaveAttribute('readonly', '')
  await expect(page.locator('button:text-is("Save changes")')).toHaveCount(0)
  await expect(page.locator('button:text-is("Add to R26.13 Reporting")')).toHaveCount(0)
  await expect(page.locator('[aria-label="Hydrated message"]')).toContainText('R26.13 Reporting')   // preview only reads
  await expect(page.locator('button:text-is("Mark sent")')).toHaveCount(0)
  await expect(page.locator('button:text-is("Create schedule")')).toHaveCount(0)
})
