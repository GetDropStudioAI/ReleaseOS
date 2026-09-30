import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-51: Calendar screen (month and week grids of trains, gates, windows; freeze windows as tinted ranges) and the ICS "Subscribe" section.
// The demo data has trains 14, 21 and 35 days after next Friday and a "Q4 close" freeze three days long starting 21 days after next Friday, so the
// specs read the dates from the API and page the calendar to the right month instead of assuming today's month.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}

const monthTitle = (iso: string) => new Date(`${iso.slice(0, 7)}-01T00:00:00Z`).toLocaleDateString('en-GB', { month: 'long', year: 'numeric', timeZone: 'UTC' })

/** Pages the month grid (Next month) until its caption shows the month of `iso`. */
async function showMonth(page: Page, iso: string) {
  const caption = page.locator('.cal-caption')
  for (let i = 0; i < 6 && (await caption.textContent()) !== monthTitle(iso); i++) await page.getByRole('button', { name: /^Next month/ }).click()
  await expect(caption).toHaveText(monthTitle(iso))
}

const demoTrain = (page: Page, title: string) =>
  page.evaluate(async t => {
    const list = await (await fetch('/api/v1/trains')).json() as { id: string; title: string; targetReleaseDate: string }[]
    return list.find(r => r.title.includes(t))!
  }, title)

const feedStatus = (page: Page, url: string) =>
  page.evaluate(async u => { const r = await fetch(u, { credentials: 'omit' }); return { status: r.status, type: r.headers.get('content-type'), text: r.status === 200 ? await r.text() : '' } }, url)

const allLink = (page: Page) => page.getByLabel('All trains link')

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme, permissions: ['clipboard-read', 'clipboard-write'] })

    test('month view shows the seeded train and a freeze range, and a click opens the train', async ({ page }) => {
      await signIn(page, `cal-${scheme}@example.com`)
      const train = await demoTrain(page, 'Card portal')
      const freeze = await page.evaluate(async () => (await (await fetch('/api/v1/freeze-windows/ahead')).json() as { name: string; startsAt: string }[]).find(f => f.name === 'Q4 close')!)

      await page.locator('nav[aria-label=Primary] a[href="/calendar"]').click()
      await expect(page).toHaveURL(/\/calendar$/)
      await expect(page.locator('h1')).toHaveText('Calendar')
      await expect(page.getByRole('button', { name: 'Month', exact: true })).toHaveAttribute('aria-pressed', 'true')
      await expect(page.locator('table.cal td.cal-today')).toHaveCount(1)              // today is marked, in words too
      await expect(page.locator('table.cal td.cal-today')).toContainText('today')

      // the freeze: tinted days, named in text
      await showMonth(page, freeze.startsAt.slice(0, 10))
      const tinted = page.locator('table.cal td.cal-freeze, table.cal td.cal-chill')
      await expect(tinted.first()).toBeVisible()
      expect(await tinted.count()).toBeGreaterThanOrEqual(1)
      await expect(page.getByTestId('freeze-label').filter({ hasText: 'Q4 close' }).first()).toContainText('▲ Freeze · Q4 close')
      await scan(page, `calendar month with a freeze (${scheme})`)

      // the train: a text entry on its target date with glyph + word status; a click opens it
      await showMonth(page, train.targetReleaseDate)
      const entry = page.getByTestId('cal-target').filter({ hasText: 'Card portal' })
      await expect(entry).toBeVisible()
      await expect(entry).toContainText(/[○◐●✓✗] (planning|gated|executing|complete|aborted)/)
      await expect(page.locator(`td[data-date="${train.targetReleaseDate}"]`)).toContainText('Card portal')
      await entry.click()
      await expect(page).toHaveURL(new RegExp(`/trains/${train.id}`))
      await expect(page.locator('svg.timeline')).toBeVisible()
    })

    test('week view, navigation buttons and the keyboard', async ({ page }) => {
      await signIn(page, `cal-kb-${scheme}@example.com`)
      const train = await demoTrain(page, 'Card portal')
      await page.goto('/calendar')
      await showMonth(page, train.targetReleaseDate)

      // arrow keys move the day focus, Page Down the month
      const cell = page.locator(`td[data-date="${train.targetReleaseDate}"]`)
      await cell.click({ position: { x: 2, y: 2 } })
      await expect(cell).toHaveAttribute('aria-selected', 'true')
      const next = new Date(`${train.targetReleaseDate}T00:00:00Z`); next.setUTCDate(next.getUTCDate() + 1)
      await page.keyboard.press('ArrowRight')
      await expect(page.locator(`td[data-date="${next.toISOString().slice(0, 10)}"]`)).toBeFocused()
      await page.keyboard.press('ArrowLeft')
      await expect(cell).toBeFocused()
      const before = await page.locator('.cal-caption').textContent()
      await page.keyboard.press('PageDown')
      await expect(page.locator('.cal-caption')).not.toHaveText(before!)
      await page.keyboard.press('PageUp')
      await expect(page.locator('.cal-caption')).toHaveText(before!)
      await cell.click({ position: { x: 2, y: 2 } })                                  // (a 31st clamps to the 30th on the way back)

      // Enter opens the first item of the day
      await page.keyboard.press('Enter')
      await expect(page).toHaveURL(new RegExp(`/trains/${train.id}`))

      // back on the calendar: week view, with the same day inside it
      await page.goto('/calendar')
      await showMonth(page, train.targetReleaseDate)
      await page.locator(`td[data-date="${train.targetReleaseDate}"]`).click({ position: { x: 2, y: 2 } })
      await page.getByRole('button', { name: 'Week', exact: true }).click()
      await expect(page.getByRole('button', { name: 'Week', exact: true })).toHaveAttribute('aria-pressed', 'true')
      await expect(page.locator('table.cal.week tbody tr')).toHaveCount(1)
      await expect(page.locator('table.cal.week td')).toHaveCount(7)
      await expect(page.getByTestId('cal-target').filter({ hasText: 'Card portal' })).toBeVisible()
      const wk = await page.locator('.cal-caption').textContent()
      await page.getByRole('button', { name: /^Next week/ }).click()
      await expect(page.locator('.cal-caption')).not.toHaveText(wk!)
      await page.getByRole('button', { name: 'Today', exact: true }).click()
      await expect(page.locator('table.cal td.cal-today')).toHaveCount(1)
      await scan(page, `calendar week (${scheme})`)
    })

    test('Subscribe: the link is shown once, copy works, rotate replaces it and kills the old one, revoke kills the new one', async ({ page }) => {
      await signIn(page, `cal-sub-${scheme}-${Date.now()}@example.com`)
      await page.goto('/calendar')
      const sub = page.getByRole('region', { name: 'Your new calendar link' })
      await expect(page.getByRole('heading', { name: 'Subscribe' })).toBeVisible()
      await expect(page.getByText('You have no calendar link.')).toBeVisible()
      await expect(sub).toHaveCount(0)

      await page.getByRole('button', { name: 'Create link' }).click()
      await expect(sub).toBeVisible()
      await expect(sub).toContainText('shown only once')
      const url1 = await allLink(page).inputValue()
      expect(url1).toMatch(/\/api\/v1\/ics\/[A-Za-z0-9_-]{43}\.ics$/)
      await scan(page, `subscribe with a new link (${scheme})`)

      await page.getByRole('button', { name: 'Copy' }).first().click()
      await expect(page.getByRole('button', { name: '✓ Copied' })).toBeVisible()
      expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(url1)

      const live = await feedStatus(page, url1)                                        // anonymous: no cookie is sent
      expect(live.status).toBe(200)
      expect(live.type).toContain('text/calendar')
      expect(live.text).toContain('BEGIN:VCALENDAR')
      expect(live.text).toContain('X-WR-CALNAME:Release trains')

      // shown once: after a reload the secret is nowhere on the page, only the row
      await page.reload()
      await expect(sub).toHaveCount(0)
      await expect(page.locator('input[aria-label$=" link"]')).toHaveCount(0)
      await expect(page.getByTestId('ics-row')).toHaveCount(1)
      await expect(page.getByTestId('ics-row')).toContainText('● active')
      expect(await page.content()).not.toContain(url1.split('/ics/')[1].slice(0, 20))

      // rotate: inline confirm, a different URL, the old one is dead at once
      await page.getByRole('button', { name: 'Rotate' }).click()
      await expect(page.getByText('The current link stops working at once')).toBeVisible()
      await page.getByRole('button', { name: 'Confirm rotate' }).click()
      await expect(sub).toBeVisible()
      const url2 = await allLink(page).inputValue()
      expect(url2).not.toBe(url1)
      expect((await feedStatus(page, url1)).status).toBe(404)
      expect((await feedStatus(page, url2)).status).toBe(200)
      await expect(page.getByTestId('ics-row')).toHaveCount(2)                          // the revoked one stays as history

      // revoke: the new link dies too and the page offers to create again
      await page.getByRole('button', { name: 'Revoke' }).click()
      await page.getByRole('button', { name: 'Confirm revoke' }).click()
      await expect(page.getByText('You have no calendar link.')).toBeVisible()
      expect((await feedStatus(page, url2)).status).toBe(404)
      await scan(page, `subscribe after revoke (${scheme})`)
    })
  })
}
