import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-119 (RT4, D47): client dates on a Production release. The Overview lists every client with its date and segment: Standard clients on the Standard date, On-Demand clients
// at the offered date (Standard + 7 days) or the date they chose, with who recorded it and when. A date inside a freeze window is an advisory beside the row. Adds its own environment,
// clients (unique names), release and freeze window (in 2031) to the shared demo database.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now() % 1_000_000

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

interface World { trainId: string; std: string; od: string; freeze: string }

/** A Production release with a Standard and an On-Demand client and a freeze window over the On-Demand client's offered date, made through the API as the signed-in Release Manager. */
async function world(page: Page, tag: string): Promise<World> {
  const names = { env: `E2E CD-CERT ${tag} ${stamp}`, std: `E2E CD-Acme ${tag} ${stamp}`, od: `E2E CD-Globex ${tag} ${stamp}`, title: `R31.03 CD ${tag} ${stamp}`, freeze: `E2E Quarter close ${tag} ${stamp}` }
  const trainId = await page.evaluate(async n => {
    const json = { 'Content-Type': 'application/json' }
    const send = async (url: string, body: unknown) => {
      const r = await fetch(url, { method: 'POST', headers: json, body: JSON.stringify(body) })
      if (!r.ok) throw new Error(`${url}: ${r.status} ${await r.text()}`)
      return r.json() as Promise<{ id: string }>
    }
    const env = await send('/api/v1/environments', { name: n.env, kind: 'Staging' })
    await send('/api/v1/clients', { name: n.std, segment: 'Standard' })
    await send('/api/v1/clients', { name: n.od, segment: 'OnDemand', certEnvironmentId: env.id })
    const types = await (await fetch('/api/v1/release-types')).json() as { id: string; name: string }[]
    const prod = types.find(t => t.name === 'Production')
    if (!prod) throw new Error('no Production release type')
    const train = await send('/api/v1/trains', { title: n.title, targetReleaseDate: '2031-03-07', riskTier: 'Low', releaseTypeId: prod.id })
    await send('/api/v1/freezes', { name: n.freeze, kind: 'Freeze', startsAt: '2031-03-13T22:00:00Z', endsAt: '2031-03-14T04:00:00Z' })
    return train.id
  }, names)
  return { trainId, std: names.std, od: names.od, freeze: names.freeze }
}

const table = (page: Page) => page.getByRole('table', { name: 'Client dates' })
const row = (page: Page, name: string) => table(page).locator('tbody tr', { hasText: name })
async function open(page: Page, w: World) {
  await page.goto(`/trains/${w.trainId}`)
  await expect(table(page)).toBeVisible()
}
/** Moves the release's Standard date through the API (the release page's own control is the timeline; the service is the same). */
async function moveStandard(page: Page, trainId: string, to: string) {
  const ok = await page.evaluate(async ([id, d]) => {
    const t = await (await fetch(`/api/v1/trains/${id}`)).json() as { version: number }
    return (await fetch(`/api/v1/trains/${id}`, { method: 'PATCH', headers: { 'Content-Type': 'application/json', 'If-Match': String(t.version) }, body: JSON.stringify({ targetReleaseDate: d }) })).ok
  }, [trainId, to] as const)
  expect(ok).toBe(true)
}

test('the release lists every client with its date and segment; the On-Demand client is offered Standard + 7 days, with the freeze as an advisory', async ({ page }) => {
  await signIn(page, 'cd-list-rm@example.com', 'ReleaseManager')
  const w = await world(page, 'list')
  await open(page, w)

  const panel = page.getByRole('region', { name: 'Client dates' })
  await expect(panel).toContainText('Production release')
  await expect(panel).toContainText('Standard date Fri 7 Mar 2031')
  await expect(row(page, w.std)).toContainText('Standard')
  await expect(row(page, w.std)).toContainText('Fri 7 Mar 2031')
  await expect(row(page, w.std)).toContainText('Standard date · Standard date · Standard wave')
  await expect(row(page, w.od)).toContainText('On-Demand')
  await expect(row(page, w.od)).toContainText('Fri 14 Mar 2031')
  await expect(row(page, w.od)).toContainText('Offered (default) · +7 days · On-Demand wave')
  await expect(row(page, w.od)).toContainText('None')                                   // no chosen date yet
  // the freeze window over 14 March is an advisory in words, with a glyph and a screen-reader prefix
  await expect(row(page, w.od)).toContainText(`Freeze window "${w.freeze}" covers this date`)
  await expect(row(page, w.od)).toContainText('(advisory)')
  await expect(row(page, w.od).locator('.sr-only', { hasText: 'Advisory:' })).toHaveCount(1)
  await expect(row(page, w.std)).not.toContainText('covers this date')
})

test('a planner records the date a client chose; moving the Standard date moves the offered date and not the chosen one; clearing asks first', async ({ page }) => {
  await signIn(page, 'cd-choose-rm@example.com', 'ReleaseManager')
  const w = await world(page, 'choose')
  await open(page, w)

  await row(page, w.od).click()
  const form = page.getByRole('group', { name: `Dates for ${w.od}` })
  const save = form.getByRole('button', { name: 'Save dates' })
  await expect(save).toBeDisabled()
  await expect(form).toContainText('Change a date first.')

  // before the Standard date: refused in words before anything is sent
  await form.getByLabel('Chosen date').fill('2031-03-01')
  await expect(save).toBeDisabled()
  await expect(form).toContainText('A chosen date cannot be earlier than the Standard date (Fri 7 Mar 2031).')

  await form.getByLabel('Chosen date').fill('2031-03-28')
  await save.click()
  await expect(page.getByTestId('announcer')).toContainText(`Dates for ${w.od} saved`)
  await expect(row(page, w.od)).toContainText('Chosen by the client')
  await expect(row(page, w.od)).toContainText('Fri 28 Mar 2031')
  await expect(row(page, w.od)).toContainText('Recorded by')
  await expect(row(page, w.od)).toContainText('UTC')                                    // when it was recorded: UTC first (D44)
  await expect(row(page, w.od)).not.toContainText('covers this date')                   // 28 March is outside the freeze

  // the Standard date moves a week: the offered date follows, the chosen date stays
  await moveStandard(page, w.trainId, '2031-03-14')
  await open(page, w)
  await expect(row(page, w.std)).toContainText('Fri 14 Mar 2031')
  await expect(row(page, w.od)).toContainText('Fri 21 Mar 2031')                        // offered: 14 + 7
  await expect(row(page, w.od)).toContainText('Fri 28 Mar 2031')                        // chosen, unchanged
  await expect(row(page, w.od)).toContainText('Chosen by the client · +14 days')

  // clearing the chosen date is a destructive act: a dialog first, Cancel changes nothing
  await row(page, w.od).click()
  await form.getByRole('button', { name: 'Clear chosen date' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('goes back to the offered date')
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(row(page, w.od)).toContainText('Chosen by the client')
  await form.getByRole('button', { name: 'Clear chosen date' }).click()
  await dialog.getByRole('button', { name: 'Clear chosen date' }).click()
  await expect(row(page, w.od)).toContainText('Offered · +7 days')
  await expect(row(page, w.od)).not.toContainText('Recorded by')
})

test('a viewer reads the dates and cannot change them', async ({ page, browser }) => {
  await signIn(page, 'cd-view-rm@example.com', 'ReleaseManager')
  const w = await world(page, 'view')
  const other = await browser.newPage()
  await signIn(other, 'cd-viewer@example.com', 'Viewer')
  await open(other, w)
  await expect(row(other, w.od)).toContainText('Fri 14 Mar 2031')
  await row(other, w.od).click()
  await expect(other.getByRole('group', { name: `Dates for ${w.od}` })).toContainText('Only an RTE or Release Manager can record client dates.')
  await expect(other.getByRole('button', { name: 'Save dates' })).toHaveCount(0)
  await other.close()
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} mode`, () => {
    test.use({ colorScheme: scheme })
    test(`the client dates table, the date form and the clear dialog are axe clean (${scheme})`, async ({ page }) => {
      await signIn(page, `cd-a11y-${scheme}@example.com`, 'ReleaseManager')
      const w = await world(page, `a11y-${scheme}`)
      await open(page, w)
      await scan(page, `Client dates (${scheme})`)
      await row(page, w.od).click()
      const form = page.getByRole('group', { name: `Dates for ${w.od}` })
      await form.getByLabel('Chosen date').fill('2031-03-21')
      await scan(page, `Client dates, the form (${scheme})`)
      await form.getByRole('button', { name: 'Save dates' }).click()
      await expect(row(page, w.od)).toContainText('Chosen by the client')
      await form.getByRole('button', { name: 'Clear chosen date' }).click()
      await expect(page.getByRole('dialog')).toBeVisible()
      await scan(page, `Client dates, the clear dialog (${scheme})`)
      await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()
    })
  })
}
