import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { primaryNav, signIn } from './support'

// REOS-120 (RT5, D46 to D49): the Portfolio & calendar and the Overview show each release's type, its place in its version, and the days its Standard and On-Demand clients get it.
// The calendar filters by release type and by client segment (Standard, On-Demand, or both together). Adds its own version, environment, clients and releases (unique names, about
// three months out so no other spec's week holds them) to the shared demo database.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now() % 1_000_000

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

/** The Monday about 100 days from now: CERT is a week before it, Production on it, and the On-Demand Production date a week after. */
function mondayAhead(): string {
  const d = new Date(Date.now() + 100 * 86_400_000)
  d.setUTCDate(d.getUTCDate() + ((8 - d.getUTCDay()) % 7))
  return d.toISOString().slice(0, 10)
}
const plus = (iso: string, n: number) => new Date(Date.parse(`${iso}T00:00:00Z`) + n * 86_400_000).toISOString().slice(0, 10)

interface World { monday: string; names: { version: string; cert: string; prod: string; beta: string; std: string; od1: string; od2: string } }

/** Orion: CERT (Monday - 7) and Production (Monday) in one version; a lone Beta. Standard client Acme, On-Demand clients Globex and Initech (offered Standard + 7). */
async function world(page: Page, tag: string): Promise<World> {
  const monday = mondayAhead()
  const names = { version: `E2E Orion ${tag} ${stamp}`, cert: `E2E Orion CERT ${tag} ${stamp}`, prod: `E2E Orion Production ${tag} ${stamp}`, beta: `E2E Lone Beta ${tag} ${stamp}`,
    std: `E2E Acme ${tag} ${stamp}`, od1: `E2E Globex ${tag} ${stamp}`, od2: `E2E Initech ${tag} ${stamp}` }
  await page.evaluate(async ([n, mon, before, after]) => {
    const json = { 'Content-Type': 'application/json' }
    const send = async (url: string, body: unknown) => {
      const r = await fetch(url, { method: 'POST', headers: json, body: JSON.stringify(body) })
      if (!r.ok) throw new Error(`${url}: ${r.status} ${await r.text()}`)
      return r.json() as Promise<{ id: string }>
    }
    const types = await (await fetch('/api/v1/release-types')).json() as { id: string; name: string }[]
    const t = (x: string) => types.find(y => y.name === x)!.id
    const env = await send('/api/v1/environments', { name: `E2E CERT env ${n.version}`, kind: 'Staging' })
    await send('/api/v1/clients', { name: n.std, segment: 'Standard' })
    await send('/api/v1/clients', { name: n.od1, segment: 'OnDemand', certEnvironmentId: env.id })
    await send('/api/v1/clients', { name: n.od2, segment: 'OnDemand', certEnvironmentId: env.id })
    const v = await send('/api/v1/versions', { name: n.version })
    const mk = (title: string, date: string, type: string, version: string | null, beta?: number) =>
      send('/api/v1/trains', { title, targetReleaseDate: date, riskTier: 'Low', releaseTypeId: t(type), betaNumber: beta ?? null, releaseVersionId: version })
    await mk(n.cert, before, 'CERT', v.id)
    await mk(n.prod, mon, 'Production', v.id)
    await mk(n.beta, after, 'Beta', null, 1)
  }, [names, monday, plus(monday, -7), plus(monday, 3)] as const)
  return { monday, names }
}

const grid = (page: Page) => page.locator('table.os-cal.week')
const rowOf = (page: Page, title: string) => grid(page).locator('tbody tr', { has: page.locator('th[scope=row]', { hasText: title }) })
const cell = (page: Page, title: string, iso: string) => rowOf(page, title).locator(`td[data-date="${iso}"]`)

async function openWeek(page: Page, iso: string) {
  await page.locator('nav[aria-label=Primary] a[href="/calendar"]').click()
  await expect(page.getByRole('heading', { level: 1, name: 'Release portfolio' })).toBeVisible()
  const target = page.locator(`table.os-cal.week td[data-date="${iso}"]`)
  for (let i = 0; i < 30 && (await target.count()) === 0; i++) await page.getByRole('button', { name: 'Next week', exact: true }).click()
  await expect(target.first()).toBeVisible()
}

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('a version\'s releases and its On-Demand client dates show on the calendar, each with its type', async ({ page }) => {
      await signIn(page, `cal-types-${scheme}@example.com`)
      const w = await world(page, scheme)
      await openWeek(page, w.monday)

      // CERT is listed for the day its On-Demand clients get it (Monday), though its own target date is the week before
      const cert = rowOf(page, w.names.cert)
      await expect(cert.locator('th[scope=row]')).toContainText('CERT')
      await expect(cert.locator('th[scope=row]')).toContainText(`${w.names.version} · CERT, 1 of 2`)
      const odCert = cell(page, w.names.cert, w.monday).getByTestId('cal-client')
      await expect(odCert).toContainText(/\d+ On-Demand clients?/)   // every On-Demand client of the workspace, the earlier specs' too
      await expect(odCert).toHaveAttribute('title', new RegExp(w.names.od1))
      await expect(odCert).toHaveAttribute('title', new RegExp(w.names.od2))
      await expect(odCert).toContainText('On-Demand')   // the chip says the segment in words
      await expect(odCert).toContainText('CERT · ')

      // Production: its target date names the type, and the Standard clients have their own card on the same day
      const prod = rowOf(page, w.names.prod)
      await expect(prod.locator('th[scope=row]')).toContainText(`${w.names.version} · Production, 2 of 2`)
      await expect(cell(page, w.names.prod, w.monday).getByTestId('cal-target')).toContainText('Target date · Production')
      const std = cell(page, w.names.prod, w.monday).getByTestId('cal-client')
      await expect(std).toContainText(/\d+ Standard clients?/)
      await expect(std).toContainText('Standard')
      await expect(std).toHaveAttribute('title', new RegExp(`${w.names.std}`))
      await scan(page, `week with client dates (${scheme})`)

      // a week later the On-Demand clients get Production
      await page.getByRole('button', { name: 'Next week', exact: true }).click()
      const od = cell(page, w.names.prod, plus(w.monday, 7)).getByTestId('cal-client')
      await expect(od).toContainText(/\d+ On-Demand clients?/)
      await expect(od).toContainText('Production')
      await expect(od).toHaveAttribute('title', new RegExp(w.names.od1))
      await expect(od).toHaveAttribute('title', new RegExp(w.names.od2))
      await od.click()
      await expect(page).toHaveURL(/\/trains\//)   // a client date opens its release
    })

    test('filters by release type and by client segment narrow the releases and their client dates', async ({ page }) => {
      await signIn(page, `cal-filter-${scheme}@example.com`)
      const w = await world(page, `f${scheme}`)
      await openWeek(page, w.monday)
      const type = page.getByLabel('Release type'), segment = page.getByLabel('Client segment')
      await expect(rowOf(page, w.names.cert)).toHaveCount(1)
      await expect(rowOf(page, w.names.prod)).toHaveCount(1)

      await type.selectOption({ label: 'CERT' })
      await expect(rowOf(page, w.names.cert)).toHaveCount(1)
      await expect(rowOf(page, w.names.prod)).toHaveCount(0)
      await type.selectOption({ label: 'All release types' })

      await segment.selectOption({ label: 'Standard clients' })
      await expect(rowOf(page, w.names.prod)).toHaveCount(1)
      await expect(rowOf(page, w.names.cert)).toHaveCount(0)               // CERT reaches On-Demand clients only
      await expect(cell(page, w.names.prod, w.monday).getByTestId('cal-client')).toContainText('Standard client')

      await segment.selectOption({ label: 'On-Demand clients' })
      await expect(rowOf(page, w.names.cert)).toHaveCount(1)
      await expect(cell(page, w.names.prod, w.monday).getByTestId('cal-client')).toHaveCount(0)   // Production's Standard day is not an On-Demand day
      await expect(cell(page, w.names.cert, w.monday).getByTestId('cal-client')).toContainText('On-Demand')

      await segment.selectOption({ label: 'Standard + On-Demand' })        // the combined view: every release with client dates, both segments
      await expect(rowOf(page, w.names.cert)).toHaveCount(1)
      await expect(rowOf(page, w.names.prod)).toHaveCount(1)
      await expect(rowOf(page, w.names.beta)).toHaveCount(0)               // a Beta has no client dates
      await expect(cell(page, w.names.prod, w.monday).getByTestId('cal-client')).toContainText('Standard client')

      await type.selectOption({ label: 'Production' })                     // both filters at once
      await expect(rowOf(page, w.names.cert)).toHaveCount(0)
      await expect(rowOf(page, w.names.prod)).toHaveCount(1)
      await scan(page, `filtered week (${scheme})`)
    })

    test('the Overview shows each release\'s type, its version chain and its client dates, and filters them by segment', async ({ page }) => {
      await signIn(page, `overview-types-${scheme}@example.com`)
      const w = await world(page, `o${scheme}`)
      await primaryNav(page).getByRole('link', { name: 'Overview' }).click()
      await expect(page.getByRole('heading', { level: 1, name: 'Overview' })).toBeVisible()
      const table = page.getByRole('table', { name: 'Release activity' })
      const row = (title: string) => table.locator('tbody tr', { hasText: title })
      await expect(row(w.names.prod)).toContainText('Production')
      await expect(row(w.names.prod)).toContainText(`${w.names.version} · Production, 2 of 2`)
      await expect(row(w.names.prod)).toContainText('Standard')
      await expect(row(w.names.prod)).toContainText('On-Demand')
      await expect(row(w.names.prod)).toContainText(/\d+ clients?/)
      await expect(row(w.names.beta)).toContainText('Beta 1')
      await expect(row(w.names.beta)).toContainText('None')                // no client dates on a Beta
      await scan(page, `overview with client dates (${scheme})`)

      await page.getByLabel('Client segment').selectOption({ label: 'Standard clients' })
      await expect(row(w.names.prod)).toHaveCount(1)
      await expect(row(w.names.prod)).not.toContainText('On-Demand')
      await expect(row(w.names.cert)).toHaveCount(0)
      await expect(row(w.names.beta)).toHaveCount(0)
    })
  })
}
