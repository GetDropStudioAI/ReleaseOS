import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { releaseRow, releasesTable, signIn } from './support'

// REOS-125 (D50): Releases is a full-width table with filters (status, team, environment, type); the 272 px Stream and its freeze footer are gone. A row opens the release page.
// The demo trains have no team, owner, type or environments, so the spec gives "Card portal" (Gated) a team, an owner, a type and two environments through the API, and leaves the others as they are.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now().toString(36)
const team = `rl${stamp}`, env = `RL Staging ${stamp}`

/** One of the four filter dropdowns (the label is visually hidden; the select is native). */
const filter = (page: Page, name: string) => page.getByRole('search', { name: 'Filter releases' }).getByLabel(name)

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

/** Gives Card portal a team, an owner, the CERT type and an environment, as the signed-in planner. Safe to run once per database; later runs find it set. */
async function describeCardPortal(page: Page) {
  return page.evaluate(async ([teamHandle, envName]) => {
    const json = { 'Content-Type': 'application/json' }
    const get = async <T,>(url: string) => (await (await fetch(url)).json()) as T
    const post = async (url: string, body: unknown) => { const r = await fetch(url, { method: 'POST', headers: json, body: JSON.stringify(body) }); if (!r.ok) throw new Error(`${url} ${r.status}`); return r.json() as Promise<{ id: string }> }
    const trains = await get<{ id: string; title: string; version: number }[]>('/api/v1/trains')
    const card = trains.find(t => t.title.includes('Card portal'))!
    const tm = await post('/api/v1/teams', { handle: teamHandle, name: `RL team ${teamHandle}`, memberIds: [] })
    const en = await post('/api/v1/environments', { name: envName, kind: 'Staging' })
    const types = await get<{ id: string; name: string }[]>('/api/v1/release-types')
    const owners = await get<{ id: string; kind: string }[]>('/api/v1/owners')
    const r = await fetch(`/api/v1/trains/${card.id}/release`, { method: 'PATCH', headers: { ...json, 'If-Match': `"${card.version}"` },
      body: JSON.stringify({ ownerTeamId: tm.id, ownerUserId: owners.find(o => o.kind === 'user')!.id, releaseTypeId: types.find(t => t.name === 'CERT')!.id, environmentIds: [en.id] }) })
    if (!r.ok) throw new Error(`release PATCH ${r.status} ${await r.text()}`)
    return card.id
  }, [team, env] as const)
}

test('the Releases page is a full-width table: key, status with blockers, type, team and owner, environments, target, gates; no Stream, no inspector', async ({ page }) => {
  await signIn(page, 'releases-rte@example.com')
  await describeCardPortal(page)
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Releases', level: 1 })).toBeVisible()
  await expect(page.locator('aside[aria-label=Stream]')).toHaveCount(0)
  await expect(page.locator('aside[aria-label=Inspector]')).toHaveCount(0)
  await expect(page.locator('.freeze-footer')).toHaveCount(0)
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toContainText('Releases')

  const heads = await releasesTable(page).locator('thead th').allTextContents()
  expect(heads).toEqual(['Release', 'Status', 'Type', 'Team and owner', 'Environments', 'Target date (UTC)', 'Gates'])

  const card = releaseRow(page, 'Card portal')
  await expect(card).toContainText(/RLS-\d+/)
  await expect(card).toContainText('Gated')
  await expect(card).toContainText('▲ 2 blockers')                       // the Stream's blocker count, in words
  await expect(card).toContainText('CERT')
  await expect(card).toContainText(`@${team}`)
  await expect(card).toContainText(env)
  await expect(card).toContainText(/\d+ of \d+ passed/)
  await expect(card).toContainText(/T[−+]\d/)
  await expect(releaseRow(page, 'Reporting')).toContainText('Planning')
  await expect(releaseRow(page, 'Reporting')).toContainText('Not set')    // nothing recorded is said so, never invented

  // open work in status order: Gated (Card portal) before Planning (Reporting)
  const rows = await releaseRow(page).allTextContents()
  expect(rows.findIndex(t => t.includes('Card portal'))).toBeLessThan(rows.findIndex(t => t.includes('Reporting')))

  // the table uses the page: far wider than the old 272 px Stream, and the table's footer says how many and when
  const box = (await releasesTable(page).boundingBox())!
  expect(box.width).toBeGreaterThan(800)
  await expect(page.getByRole('main')).toContainText(/\d+ of \d+ releases · complete and aborted releases stay listed for 30 days · loaded \d+ \w+ \d{4}, \d\d:\d\d UTC/)
})

test('filters: status, team, environment, type and search narrow the table, combine, say so when nothing matches, and clear', async ({ page }) => {
  await signIn(page, 'releases-filter@example.com')
  const total = await releaseRow(page).count()
  const choose = (name: string, label: string) => filter(page, name).selectOption({ label })

  await filter(page, 'Status').selectOption({ label: 'Gated' })
  await expect(releaseRow(page, 'Card portal')).toBeVisible()
  await expect(releaseRow(page, 'Reporting')).toHaveCount(0)
  for (const t of await releaseRow(page).allTextContents()) expect(t).toContain('Gated')
  await expect(page).toHaveURL(/status=Gated/)

  await choose('Team', `@${team}`)
  await choose('Environment', env)
  await choose('Type', 'CERT')
  await expect(releaseRow(page)).toHaveCount(1)
  await expect(releaseRow(page, 'Card portal')).toBeVisible()
  await expect(page.getByRole('main')).toContainText('1 of ')

  // a combination that matches nothing is said in words and offers the way out
  await filter(page, 'Status').selectOption({ label: 'Planning' })
  await expect(releaseRow(page)).toHaveCount(0)
  await expect(releasesTable(page)).toContainText('No release matches these filters.')

  // the filters survive a reload (the address carries them) and Clear filters empties them
  await page.reload()
  await expect(filter(page, 'Team')).toHaveValue(/.+/)
  await expect(releasesTable(page)).toContainText('No release matches these filters.')
  await page.getByRole('button', { name: 'Clear filters' }).click()
  await expect(releaseRow(page)).toHaveCount(total)
  await expect(page.getByRole('button', { name: 'Clear filters' })).toHaveCount(0)
  await expect(page).not.toHaveURL(/status=/)

  // "No team" finds the releases with none recorded; search by team handle and by title
  await choose('Team', 'No team')
  await expect(releaseRow(page, 'Card portal')).toHaveCount(0)
  await expect(releaseRow(page, 'Reporting')).toBeVisible()
  await page.getByRole('button', { name: 'Clear filters' }).click()
  await page.getByLabel('Search the release list').fill(team.toUpperCase())
  await expect(releaseRow(page)).toHaveCount(1)
  await page.getByLabel('Search the release list').fill('reporting')
  await expect(releaseRow(page)).toHaveCount(1)
  await expect(releaseRow(page, 'Reporting')).toBeVisible()
})

test('a row opens the release page by mouse, by key link and from the keyboard; the sidebar, the browser and deep links come back', async ({ page }) => {
  await signIn(page, 'releases-open@example.com')
  await page.getByLabel('Search the release list').fill('Card portal')

  // keyboard: Tab to the row, Enter opens (Down/Up move between rows)
  const row = releaseRow(page, 'Card portal')
  await row.focus()
  await expect(row).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/\/trains\/[0-9a-f-]{36}$/)
  await expect(page.locator('main h1')).toContainText('Card portal')
  await expect(page.getByRole('group', { name: 'Release context' })).toContainText('Gated')
  await expect(page.locator('aside[aria-label=Inspector]')).toBeVisible()
  const trainUrl = page.url()

  // the sidebar's Releases returns to the list, filter as it was left; Back and Forward walk the same way
  await page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: /^Releases$/ }).click()
  await expect(page.getByRole('heading', { name: 'Releases', level: 1 })).toBeVisible()
  await expect(page.getByLabel('Search the release list')).toHaveValue('Card portal')
  await page.goBack()
  await expect(page).toHaveURL(trainUrl)
  await expect(page.locator('main h1')).toContainText('Card portal')

  // the key link is a real link to /trains/{id}, and a click opens it too
  await page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: /^Releases$/ }).click()
  const link = releaseRow(page, 'Card portal').getByRole('link', { name: /^Open RLS-/ })
  await expect(link).toHaveAttribute('href', /\/trains\/[0-9a-f-]{36}$/)
  await link.click()
  await expect(page).toHaveURL(trainUrl)

  // deep links the Stream used to serve keep working: the release, a gate inside it, and the live runbook
  const id = trainUrl.split('/').pop()!
  const gateId = await page.evaluate(async i => ((await (await fetch(`/api/v1/trains/${i}`)).json()) as { gates: { id: string }[] }).gates[0].id, id)
  await page.goto(`/trains/${id}/gates/${gateId}`)
  await expect(page.locator('aside[aria-label=Inspector] h2').first()).toBeVisible()
  await page.goto(`/trains/${id}/live`)
  await expect(page.locator('main h1')).toBeVisible()
  await page.goto(`/trains/${id}`)
  await expect(page.locator('main h1')).toContainText('Card portal')

  // a row also opens on a click anywhere in it (the list comes back with the filter it was left with: clear it)
  await page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: /^Releases$/ }).click()
  await page.getByRole('button', { name: 'Clear filters' }).click()
  await releaseRow(page, 'Reporting').getByRole('cell').nth(2).click()
  await expect(page.locator('main h1')).toContainText('Reporting')
})

test('a Viewer reads, filters and opens releases but has no New train', async ({ page }) => {
  await signIn(page, 'releases-viewer@example.com', 'Viewer')
  await expect(page.getByRole('button', { name: 'New train' })).toHaveCount(0)
  await filter(page, 'Status').selectOption({ label: 'Gated' })
  await expect(releaseRow(page, 'Card portal')).toBeVisible()
  await releaseRow(page, 'Card portal').click()
  await expect(page.locator('main h1')).toContainText('Card portal')
})

test('an RTE sees New train on the page and it opens the drawer beside the table', async ({ page }) => {
  await signIn(page, 'releases-new@example.com')
  await page.getByRole('main').getByRole('button', { name: 'New train', exact: true }).click()
  await expect(page.locator('aside[aria-label=Inspector] h2', { hasText: 'New train' })).toBeVisible()
  await expect(releasesTable(page)).toBeVisible()                        // the list stays beside the drawer
  await page.locator('aside[aria-label=Inspector]').locator('button:text-is("Discard")').click()
  await expect(page.locator('aside[aria-label=Inspector]')).toHaveCount(0)
})

test('the list says what it is doing: a failed load with Retry, and an empty workspace', async ({ page }) => {
  await signIn(page, 'releases-states@example.com')
  await page.route('**/api/v1/trains', r => r.request().method() === 'GET' ? r.fulfill({ status: 500, contentType: 'application/json', body: '{"message":"boom"}' }) : r.continue())
  await page.reload()                                                     // the rows are fetched again on load and fail
  await expect(page.getByRole('status').filter({ hasText: 'The releases could not be loaded' })).toBeVisible()
  await page.unroute('**/api/v1/trains')
  await page.getByRole('button', { name: 'Retry' }).click()
  await expect(releaseRow(page).first()).toBeVisible()

  await page.route('**/api/v1/trains', r => r.request().method() === 'GET' ? r.fulfill({ status: 200, contentType: 'application/json', body: '[]' }) : r.continue())
  await page.reload()
  await expect(page.getByText('No releases yet')).toBeVisible()
  await expect(page.getByRole('main').getByRole('button', { name: 'New train' }).first()).toBeVisible()
  await expect(page.getByRole('link', { name: 'import trains from a CSV' })).toBeVisible()
  await scan(page, 'releases (empty)')
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('the Releases page is axe clean and its controls are reachable from the keyboard with a visible focus', async ({ page }) => {
      await signIn(page, `releases-a11y-${scheme}@example.com`)
      await expect(releaseRow(page).first()).toBeVisible()
      await scan(page, `releases (${scheme})`)
      await filter(page, 'Status').selectOption({ label: 'Gated' })
      await expect(releaseRow(page).first()).toBeVisible()
      await scan(page, `releases filtered (${scheme})`)
      await filter(page, 'Status').selectOption({ label: 'Planning' })
      await page.getByLabel('Search the release list').fill('zzz-no-such-release')
      await expect(releasesTable(page)).toContainText('No release matches these filters.')
      await scan(page, `releases, nothing matches (${scheme})`)
      await page.getByRole('button', { name: 'Clear filters' }).click()

      // Tab order: search, the four filters, then the rows (each a stop with a visible ring and a name)
      await page.getByLabel('Search the release list').focus()
      const seen: string[] = []
      for (let i = 0; i < 6; i++) {
        const info = await page.evaluate(() => {
          const el = document.activeElement as HTMLElement, cs = getComputedStyle(el)
          const name = el.getAttribute('aria-label') || (el as HTMLInputElement).labels?.[0]?.textContent || el.textContent || ''
          return { tag: el.tagName.toLowerCase(), name: name.replace(/\s+/g, ' ').trim().slice(0, 30), ring: [el, el.closest('.os-search, .os-filter')].some(x => { if (!x) return false; const c = getComputedStyle(x); return (c.outlineStyle !== 'none' && parseFloat(c.outlineWidth) > 0) || c.boxShadow !== 'none' }) }
        })
        expect(info.name.length, `a stop without a name: ${JSON.stringify(info)}`).toBeGreaterThan(0)
        expect(info.ring, `no visible focus on ${JSON.stringify(info)}`).toBe(true)
        seen.push(info.tag)
        await page.keyboard.press('Tab')
      }
      expect(seen.slice(0, 5)).toEqual(['input', 'select', 'select', 'select', 'select'])
      expect(seen[5]).toBe('tr')
      await page.keyboard.press('ArrowDown')
      expect(await page.evaluate(() => document.activeElement?.tagName.toLowerCase())).toBe('tr')
    })
  })
}
