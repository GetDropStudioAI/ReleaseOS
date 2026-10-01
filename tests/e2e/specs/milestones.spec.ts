import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn, trainByTitle } from './support'

// Train milestones (Q-0840..Q-0846): add one in the workspace, see its ◆ on the gate timeline and on the Calendar, mark it done, remove it with the inline
// confirm; axe in both themes on the workspace and the calendar.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}

const monthTitle = (iso: string) => new Date(`${iso.slice(0, 7)}-01T00:00:00Z`).toLocaleDateString('en-GB', { month: 'long', year: 'numeric', timeZone: 'UTC' })
const minusDays = (iso: string, n: number) => new Date(Date.parse(`${iso}T00:00:00Z`) - n * 86_400_000).toISOString().slice(0, 10)

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('add a milestone, see it on the timeline and the calendar, mark it done, remove it', async ({ page }) => {
      await signIn(page, `ms-${scheme}@example.com`)
      const t = await trainByTitle(page, 'Card portal')
      const target = await page.evaluate(async id => (await (await fetch(`/api/v1/trains/${id}`)).json() as { targetReleaseDate: string }).targetReleaseDate, t.id)
      const due = minusDays(target, 3)
      const name = `UAT sign-off ${scheme} ${Date.now() % 100000}`

      await page.goto(`/trains/${t.id}`)
      const section = page.locator('section[aria-label=Milestones]')
      await expect(section).toBeVisible()
      await section.getByRole('button', { name: 'Add milestone' }).click()
      const form = section.getByRole('group', { name: 'New milestone' })
      await form.getByLabel('Milestone').fill(name)
      await form.getByLabel('Date').fill(due)
      await form.getByLabel('Note').fill('Business sign-off in the UAT room')
      await form.getByRole('button', { name: 'Add', exact: true }).click()

      const row = section.getByTestId('milestone-row').filter({ hasText: name })
      await expect(row).toBeVisible()
      await expect(row).toContainText('◆ due')
      await expect(row).toContainText('Business sign-off in the UAT room')
      const marker = page.locator('section[aria-label="Gate timeline"] [data-testid=timeline-milestone]', { hasText: name })
      await expect(marker).toHaveAttribute('aria-label', new RegExp(`^Milestone ${name}, .*, due$`))
      await expect(page.locator('section[aria-label="Gate timeline"] h2')).toHaveText('Gates and milestones · business days to target')
      await scan(page, `train workspace with a milestone (${scheme})`)

      // the calendar shows it on its date, and a click opens the train
      await page.locator('nav[aria-label=Primary] a[href="/calendar"]').click()
      await expect(page.locator('h1')).toHaveText('Calendar')
      const caption = page.locator('.cal-caption')
      for (let i = 0; i < 6 && (await caption.textContent()) !== monthTitle(due); i++) await page.getByRole('button', { name: /^Next month/ }).click()
      await expect(caption).toHaveText(monthTitle(due))
      const entry = page.locator(`td[data-date="${due}"]`).getByTestId('cal-milestone').filter({ hasText: name })
      await expect(entry).toBeVisible()
      await expect(entry).toContainText('◆')
      await expect(entry).toContainText('open')
      await scan(page, `calendar with a milestone (${scheme})`)
      await entry.click()
      await expect(page).toHaveURL(new RegExp(`/trains/${t.id}`))

      // mark done: the state word, the done time and the timeline all say so
      await row.getByRole('button', { name: 'Mark done' }).click()
      await expect(row).toContainText('✓ done')
      await expect(marker).toHaveAttribute('aria-label', /, done$/)
      await expect(row.getByRole('button', { name: 'Mark not done' })).toBeVisible()
      await scan(page, `train workspace with a done milestone (${scheme})`)

      // remove: an inline confirm, never a modal
      await row.getByRole('button', { name: `Remove ${name}` }).click()
      const confirm = section.getByRole('group', { name: `Confirm removing ${name}` })
      await expect(confirm).toContainText('Remove')
      await confirm.getByRole('button', { name: 'Confirm remove' }).click()
      await expect(section.getByTestId('milestone-row').filter({ hasText: name })).toHaveCount(0)
      await expect(marker).toHaveCount(0)
    })
  })
}

test('a Viewer sees milestones but has no add, edit, done or remove actions', async ({ page, browser }) => {
  const rteCtx = await browser.newContext()
  const rte = await rteCtx.newPage()
  await signIn(rte, 'ms-rte@example.com')
  const t = await trainByTitle(rte, 'Card portal')
  const name = `Code complete ${Date.now() % 100000}`
  const target = await rte.evaluate(async id => (await (await fetch(`/api/v1/trains/${id}`)).json() as { targetReleaseDate: string }).targetReleaseDate, t.id)
  const created = await rte.evaluate(async ([id, n, d]) => (await (await fetch(`/api/v1/trains/${id}/milestones`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: n, dueOn: d }) })).json()) as { id: string; version: number },
    [t.id, name, minusDays(target, 5)] as const)
  try {
    await signIn(page, 'ms-viewer@example.com', 'Viewer')
    await page.goto(`/trains/${t.id}`)
    const section = page.locator('section[aria-label=Milestones]')
    const row = section.getByTestId('milestone-row').filter({ hasText: name })
    await expect(row).toBeVisible()
    await expect(page.locator('section[aria-label="Gate timeline"] [data-testid=timeline-milestone]', { hasText: name })).toHaveCount(1)
    await expect(section.getByRole('button', { name: 'Add milestone' })).toHaveCount(0)
    await expect(row.getByRole('button')).toHaveCount(0)
  } finally {
    // leave the shared demo database as it was
    await rte.evaluate(async ([id, v]) => { await fetch(`/api/v1/milestones/${id}`, { method: 'DELETE', headers: { 'If-Match': String(v) } }) }, [created.id, created.version] as const)
    await rteCtx.close()
  }
})
