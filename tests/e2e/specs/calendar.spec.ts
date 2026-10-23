import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-96 / REOS-134 (REOS-51 underneath): the Portfolio & calendar screen. Week calendar, Month, Timeline and Release list tabs, the filters, the upcoming windows and the
// capacity panel, and the Subscribe panel for the ICS feed. Every day is a UTC day. The demo data has trains 14, 21 and 35 days after next Friday, each with a 06:00 to
// 10:00 UTC window on its target date, and a "Q4 close" freeze three days long starting 21 days after next Friday; the specs read the dates from the API and page to them.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}

const heading = (page: Page) => page.getByRole('heading', { level: 1, name: 'Release portfolio' })
const title = (page: Page) => page.getByTestId('cal-title')
const tab = (page: Page, name: string) => page.getByRole('tab', { name, exact: false })

async function openPortfolio(page: Page) {
  await page.locator('nav[aria-label=Primary] a[href="/calendar"]').click()
  await expect(page).toHaveURL(/\/calendar$/)
  await expect(heading(page)).toBeVisible()
  await expect(page.getByTestId('calendar-grid')).toBeVisible()   // the grid keeps its header, and today's column, when the week is empty
}

const demoTrain = (page: Page, text: string) =>
  page.evaluate(async t => {
    const list = await (await fetch('/api/v1/trains')).json() as { id: string; title: string; status: string; targetReleaseDate: string }[]
    return list.find(r => r.title.includes(t))!
  }, text)

/** Presses Next until the week (or month) grid holds a cell for `iso`. */
async function pageTo(page: Page, iso: string, unit: 'week' | 'month') {
  const cell = page.locator(`td[data-date="${iso}"]`)
  for (let i = 0; i < 12 && (await cell.count()) === 0; i++) await page.getByRole('button', { name: `Next ${unit}`, exact: true }).click()
  await expect(cell.first()).toBeVisible()
}

const feedStatus = (page: Page, url: string) =>
  page.evaluate(async u => { const r = await fetch(u, { credentials: 'omit' }); return { status: r.status, type: r.headers.get('content-type'), text: r.status === 200 ? await r.text() : '' } }, url)
const allLink = (page: Page) => page.getByLabel('All trains link')

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme, permissions: ['clipboard-read', 'clipboard-write'] })

    test('week calendar: UTC range, today marked in words, a window card with its status, a click opens the release', async ({ page }) => {
      await signIn(page, `cal-${scheme}@example.com`)
      const train = await demoTrain(page, 'Card portal')
      await openPortfolio(page)
      await expect(tab(page, 'Week calendar')).toHaveAttribute('aria-selected', 'true')
      await expect(page.getByText('UTC', { exact: true }).first()).toBeVisible()
      await expect(page.locator('table.os-cal.week th.today')).toHaveCount(1)
      await expect(page.locator('table.os-cal.week th.today')).toContainText('Today')
      await expect(page.locator('table.os-cal.week tbody td').first()).toBeVisible()
      await scan(page, `week calendar (${scheme})`)

      await pageTo(page, train.targetReleaseDate, 'week')
      const cell = page.locator(`table.os-cal.week td[data-date="${train.targetReleaseDate}"]`).filter({ hasText: 'Card portal' }).first()
      const card = cell.getByTestId('cal-window')
      await expect(card).toBeVisible()
      await expect(card).toContainText('06:00–10:00 UTC')
      await expect(card).toContainText(train.status)                                   // the status is a word in the card, never colour alone
      await expect(cell.getByTestId('cal-target')).toContainText('Target date')
      await scan(page, `week calendar with a release (${scheme})`)
      await card.click()
      await expect(page).toHaveURL(new RegExp(`/trains/${train.id}`))
      await expect(page.locator('svg.timeline')).toBeVisible()
    })

    test('week calendar is keyboard navigable: arrows move day and release, Page Down the week, Enter opens', async ({ page }) => {
      await signIn(page, `cal-kb-${scheme}@example.com`)
      const train = await demoTrain(page, 'Card portal')
      await openPortfolio(page)
      await pageTo(page, train.targetReleaseDate, 'week')
      const own = page.locator(`table.os-cal.week td[data-date="${train.targetReleaseDate}"]`).filter({ hasText: 'Card portal' }).first()
      await own.click({ position: { x: 2, y: 2 } })
      await expect(own).toHaveAttribute('aria-selected', 'true')
      await expect(own).toHaveAttribute('aria-label', /Card portal, .*, \d+ items?/)
      const row = await own.getAttribute('data-row')
      const iso = new Date(`${train.targetReleaseDate}T00:00:00Z`)
      const day = (n: number) => { const d = new Date(iso); d.setUTCDate(d.getUTCDate() + n); return d.toISOString().slice(0, 10) }
      await page.keyboard.press('ArrowRight')
      await expect(page.locator(`table.os-cal.week td[data-date="${day(1)}"][data-row="${row}"]`)).toBeFocused()
      await page.keyboard.press('ArrowLeft')
      await expect(own).toBeFocused()
      const before = await title(page).textContent()
      await page.keyboard.press('PageDown')
      await expect(title(page)).not.toHaveText(before!)
      // the calendar moves focus to the new week's cell once that week's data has loaded; a key pressed before that lands on the body and is ignored
      await expect(page.locator('table.os-cal td:focus')).toHaveCount(1)
      await page.keyboard.press('PageUp')
      await expect(title(page)).toHaveText(before!)
      await expect(page.locator(`table.os-cal.week td[data-date="${train.targetReleaseDate}"][data-row="${row}"]`)).toBeFocused()
      // rows: Down moves to the next release row when there is one, and back Up
      const rows = await page.locator('table.os-cal.week tbody tr').count()
      if (rows > 1) {
        await page.keyboard.press('ArrowDown')
        const moved = await page.evaluate(() => (document.activeElement as HTMLElement | null)?.getAttribute('data-row'))
        if (moved !== row) { await page.keyboard.press('ArrowUp'); await expect(own).toBeFocused() }
      }
      await page.keyboard.press('Enter')
      await expect(page).toHaveURL(new RegExp(`/trains/${train.id}`))
    })

    test('Previous, Today and Next move the week; the title says the range', async ({ page }) => {
      await signIn(page, `cal-nav-${scheme}@example.com`)
      await openPortfolio(page)
      const now = await title(page).textContent()
      expect(now).toMatch(/^\d{1,2}.*20\d\d$/)
      await page.getByRole('button', { name: 'Next week', exact: true }).click()
      await expect(title(page)).not.toHaveText(now!)
      await page.getByRole('button', { name: 'Previous week', exact: true }).click()
      await page.getByRole('button', { name: 'Previous week', exact: true }).click()
      await page.getByRole('button', { name: 'Today', exact: true }).click()
      await expect(title(page)).toHaveText(now!)
      await expect(page.locator('table.os-cal.week th.today')).toHaveCount(1)
    })

    test('the filters narrow the calendar, the upcoming table and the release list together', async ({ page }) => {
      await signIn(page, `cal-filter-${scheme}@example.com`)
      const train = await demoTrain(page, 'Card portal')
      await openPortfolio(page)
      await pageTo(page, train.targetReleaseDate, 'week')
      await expect(page.locator('table.os-cal.week').getByText('Card portal').first()).toBeVisible()
      const upcoming = page.getByRole('table', { name: 'Upcoming release windows' })
      await expect(upcoming).toContainText('Card portal')

      const other = ['Planning', 'Gated', 'Executing'].find(s => s !== train.status)!
      await page.getByRole('combobox', { name: 'Status', exact: true }).selectOption(other)
      await expect(page.locator('table.os-cal.week').getByText('Card portal')).toHaveCount(0)
      await expect(upcoming).not.toContainText('Card portal')
      await tab(page, 'Release list').click()
      await expect(page.getByRole('table', { name: 'Open releases' })).not.toContainText('Card portal')

      await page.getByRole('combobox', { name: 'Status', exact: true }).selectOption(train.status)
      await expect(page.getByRole('table', { name: 'Open releases' })).toContainText('Card portal')
      await page.getByRole('combobox', { name: 'Status', exact: true }).selectOption('all')
      await page.getByRole('combobox', { name: 'Risk tier', exact: true }).selectOption('Low')
      await expect(page.getByRole('table', { name: 'Open releases' })).not.toContainText('Card portal')   // Card portal is Moderate
      await scan(page, `filtered release list (${scheme})`)
    })

    test('month view shows the freeze as tinted days named in text; Timeline and Release list open the release', async ({ page }) => {
      await signIn(page, `cal-month-${scheme}@example.com`)
      const train = await demoTrain(page, 'Card portal')
      const freeze = await page.evaluate(async () => (await (await fetch('/api/v1/freeze-windows/ahead')).json() as { name: string; startsAt: string }[]).find(f => f.name === 'Q4 close')!)
      await openPortfolio(page)

      await tab(page, 'Month').click()
      await expect(tab(page, 'Month')).toHaveAttribute('aria-selected', 'true')
      await expect(page.locator('table.os-cal.month td.today')).toHaveCount(1)
      await expect(page.locator('table.os-cal.month td.today')).toContainText('Today')
      await pageTo(page, freeze.startsAt.slice(0, 10), 'month')
      const tinted = page.locator('table.os-cal.month td.tint-freeze, table.os-cal.month td.tint-chill')
      await expect(tinted.first()).toBeVisible()
      await expect(page.getByTestId('freeze-label').filter({ hasText: 'Q4 close' }).first()).toContainText('▲ Freeze · Q4 close')
      await scan(page, `month with a freeze (${scheme})`)
      await pageTo(page, train.targetReleaseDate, 'month')
      const entry = page.locator(`td[data-date="${train.targetReleaseDate}"]`).getByTestId('cal-target').filter({ hasText: 'Card portal' })
      await expect(entry).toBeVisible()
      await expect(entry).toContainText(train.status)

      await tab(page, 'Release list').click()
      const list = page.getByRole('table', { name: 'Open releases' })
      await expect(list).toContainText('Card portal')
      await expect(list.getByRole('row', { name: /Card portal/ })).toContainText(/blockers?/)
      await scan(page, `release list (${scheme})`)

      await tab(page, 'Timeline').click()
      await page.getByRole('button', { name: 'Today', exact: true }).click()          // (the month paging above moved the focus date)
      await expect(page.getByRole('table', { name: 'Release timeline' })).toBeVisible()
      const bar = page.getByRole('row').filter({ hasText: 'Card portal' }).getByTestId('timeline-bar')
      for (let i = 0; i < 12; i++) {
        await bar.first().waitFor({ timeout: 3000 }).catch(() => undefined)            // the period's data may still be loading
        if (await bar.count()) break
        const was = await title(page).textContent()
        await page.getByRole('button', { name: 'Next 4 weeks', exact: true }).click()
        await expect(title(page)).not.toHaveText(was!)
      }
      await expect(bar).toBeVisible()
      await scan(page, `timeline (${scheme})`)
      await bar.click()
      await expect(page).toHaveURL(new RegExp(`/trains/${train.id}`))
    })

    test('capacity and constraints: conflicts are flagged and Resolve opens the release; the next freeze is named', async ({ page }) => {
      await signIn(page, `cal-cap-${scheme}@example.com`)
      await openPortfolio(page)
      const panel = page.getByRole('region', { name: 'Capacity & constraints' })
      await expect(panel).toBeVisible()
      await expect(panel).toContainText('Next change freeze')
      await expect(panel).toContainText(/Q4 close|None in the next 45 days/)
      await expect(panel.getByText(/scheduling conflicts?|No scheduling conflicts/).first()).toBeVisible()
      await expect(page.getByRole('region', { name: 'Upcoming release windows' })).toBeVisible()
      await scan(page, `capacity panel (${scheme})`)
      const resolve = panel.getByRole('button', { name: 'Resolve →' })
      if (await resolve.count()) { await resolve.click(); await expect(page).toHaveURL(/\/trains\//) }
    })

    test('Export calendar goes to the Subscribe panel; Plan release opens the New train drawer', async ({ page }) => {
      await signIn(page, `cal-actions-${scheme}@example.com`)
      await openPortfolio(page)
      await page.getByRole('button', { name: 'Export calendar' }).click()
      await expect(page.locator('#subscribe')).toBeFocused()
      await page.getByRole('button', { name: 'Plan release' }).click()
      await expect(page).toHaveURL(/\/$/)
      await expect(page.locator('h2', { hasText: 'New train' }).first()).toBeVisible()
    })

    test('a Viewer sees the calendar but has no Plan release', async ({ page }) => {
      await signIn(page, `cal-viewer-${scheme}@example.com`, 'Viewer')
      await openPortfolio(page)
      await expect(page.getByRole('button', { name: 'Export calendar' })).toBeVisible()
      await expect(page.getByRole('button', { name: 'Plan release' })).toHaveCount(0)
    })

    test('Subscribe: the link is shown once, copy works, rotate replaces it and kills the old one, revoke kills the new one', async ({ page }) => {
      await signIn(page, `cal-sub-${scheme}-${Date.now()}@example.com`)
      await openPortfolio(page)
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
      const links = page.getByRole('table', { name: 'Calendar links' })
      await expect(links.locator('tbody tr')).toHaveCount(1)
      await expect(links).toContainText('Active')
      expect(await page.content()).not.toContain(url1.split('/ics/')[1].slice(0, 20))

      // rotate: a confirmation dialog, a different URL, the old one is dead at once
      await page.getByRole('button', { name: 'Rotate' }).click()
      const dialog = page.getByRole('dialog', { name: 'Rotate your calendar link?' })
      await expect(dialog.getByText('The current link stops working at once')).toBeVisible()
      await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
      await scan(page, `rotate confirmation (${scheme})`)
      await dialog.getByRole('button', { name: 'Confirm rotate' }).click()
      await expect(sub).toBeVisible()
      const url2 = await allLink(page).inputValue()
      expect(url2).not.toBe(url1)
      expect((await feedStatus(page, url1)).status).toBe(404)
      expect((await feedStatus(page, url2)).status).toBe(200)
      await expect(links.locator('tbody tr')).toHaveCount(2)                            // the revoked one stays as history

      // revoke: the new link dies too and the panel offers to create again
      await page.getByRole('button', { name: 'Revoke' }).click()
      await page.getByRole('dialog', { name: 'Revoke your calendar link?' }).getByRole('button', { name: 'Confirm revoke' }).click()
      await expect(page.getByText('You have no calendar link.')).toBeVisible()
      expect((await feedStatus(page, url2)).status).toBe(404)
      await scan(page, `subscribe after revoke (${scheme})`)
    })
  })
}
