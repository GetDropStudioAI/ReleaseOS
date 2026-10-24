import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, primaryNav, signIn } from './support'

// REOS-117 (RT2, D48): versions. A version links the releases of one product version in a chain (Beta 1 to 3, CERT, PenTest Cert, Production). The chain shows on the release page,
// the calendar and the overview; moving one release's date flags a later release that now comes too soon (an advisory, nothing blocked); the next release is created from the previous
// one. Teams & settings has a Versions tab (archive behind a confirmation). Adds its own versions and releases (unique names) to the shared demo database.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now() % 1_000_000
const day = (n: number) => new Date(Date.now() + n * 86_400_000).toISOString().slice(0, 10)

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

/** Creates a version with Beta 1 (day 2), Beta 2 (day 9) and CERT (day 20), through the API as the signed-in planner. Returns the ids. */
async function makeChain(page: Page, name: string) {
  return page.evaluate(async ([n, d1, d2, d3]) => {
    const json = { 'Content-Type': 'application/json' }
    const send = async (url: string, body: unknown) => {
      const r = await fetch(url, { method: 'POST', headers: json, body: JSON.stringify(body) })
      if (!r.ok) throw new Error(`${url}: ${r.status} ${await r.text()}`)
      return r.json() as Promise<{ id: string; version: number }>
    }
    const types = await (await fetch('/api/v1/release-types')).json() as { id: string; name: string }[]
    const t = (x: string) => types.find(y => y.name === x)!.id
    const v = await send('/api/v1/versions', { name: n })
    const mk = (title: string, date: string, type: string, beta?: number) => send('/api/v1/trains', { title, targetReleaseDate: date, riskTier: 'Low', releaseTypeId: t(type), betaNumber: beta ?? null, releaseVersionId: v.id })
    const b1 = await mk(`${n} Beta 1`, d1, 'Beta', 1), b2 = await mk(`${n} Beta 2`, d2, 'Beta', 2), cert = await mk(`${n} CERT`, d3, 'CERT')
    return { version: v.id, b1: b1.id, b2: b2.id, cert: cert.id }
  }, [name, day(2), day(9), day(20)] as const)
}

const context = (page: Page) => page.getByRole('group', { name: 'Release context' })
const chain = (page: Page, name: string) => page.getByRole('list', { name: new RegExp(`^${name}:`) })
const versionsTable = (page: Page) => page.getByRole('table', { name: 'Versions' })
const versionRow = (page: Page, name: string | RegExp) => versionsTable(page).locator('tbody tr', { hasText: name })

async function openDependencies(page: Page, trainId: string) {
  await openPage(page, `/trains/${trainId}`)
  const tab = page.getByRole('tab', { name: /^Dependencies/ })
  await tab.click()
  await expect(tab).toHaveAttribute('aria-selected', 'true')
}

test('a version\'s releases show in order on the release page, with the release being looked at marked', async ({ page }) => {
  const name = `E2E Orion ${stamp}a`
  await signIn(page, 'ver-order-rm@example.com', 'ReleaseManager')
  const ids = await makeChain(page, name)
  await openDependencies(page, ids.b2)
  const strip = chain(page, name).first()
  await expect(strip.locator('li')).toHaveCount(3)
  await expect(strip.locator('li').nth(0)).toContainText('Beta 1')
  await expect(strip.locator('li').nth(1)).toContainText('Beta 2')
  await expect(strip.locator('li').nth(1)).toContainText('(this release)')
  await expect(strip.locator('li').nth(1)).toHaveAttribute('aria-current', 'step')
  await expect(strip.locator('li').nth(2)).toContainText('CERT')
  await expect(strip.locator('li').nth(2)).toContainText('Planning')   // the status is a word, not only a colour
  await expect(page.getByRole('region', { name: 'Version' }).or(page.locator('section[aria-label=Version]'))).toContainText('Beta 2, 2 of 3')
  // the context row names the version and the place, and shows the compact chain
  await expect(context(page)).toContainText(name)
  await expect(context(page)).toContainText('Beta 2, 2 of 3')
  await expect(page.getByRole('list', { name: new RegExp(`^${name}:`) })).toHaveCount(2)   // compact under the context row, full on the Dependencies tab
  await expect(page.getByRole('tab', { name: /^Dependencies/ })).toBeVisible()
})

test('moving a release\'s date flags the later release that now comes too soon, as an advisory that blocks nothing', async ({ page }) => {
  const name = `E2E Orion ${stamp}b`
  await signIn(page, 'ver-slip-rm@example.com', 'ReleaseManager')
  const ids = await makeChain(page, name)
  // Beta 1 slips to a week after Beta 2's date
  const status = await page.evaluate(async ([id, to]) => {
    const t = await (await fetch(`/api/v1/trains/${id}`)).json() as { version: number }
    const r = await fetch(`/api/v1/trains/${id}`, { method: 'PATCH', headers: { 'Content-Type': 'application/json', 'If-Match': String(t.version) }, body: JSON.stringify({ targetReleaseDate: to }) })
    return r.status
  }, [ids.b1, day(16)] as const)
  expect(status).toBe(200)

  await openDependencies(page, ids.b2)
  const notice = page.getByRole('list', { name: 'Chain advisories' })
  await expect(notice).toContainText('Beta 2 comes too soon after Beta 1')
  await expect(notice).toContainText('Advisory only: nothing is blocked')
  await expect(chain(page, name).first().locator('li').nth(1)).toContainText('too soon')
  await expect(page.getByRole('region', { name: 'Version' }).or(page.locator('section[aria-label=Version]'))).toContainText('1 advisory')
  // the same advisory sits in the readiness list of the Overview tab, in the advisory style
  await page.getByRole('tab', { name: /^Overview/ }).click()
  await expect(page.getByRole('list').filter({ hasText: 'To reach' }).or(page.locator('section[aria-label=Readiness]'))).toContainText(/Version: Beta 2 is planned for/)
  // On Beta 1's own page the slip shows against the next release (the chain lists it), but Beta 1 itself has no advisory in its readiness list
  await openDependencies(page, ids.b1)
  await expect(page.getByRole('list', { name: 'Chain advisories' })).toContainText('Beta 2 comes too soon after Beta 1')
  await page.getByRole('tab', { name: /^Overview/ }).click()
  await expect(page.locator('main')).not.toContainText('Version: Beta 2 is planned for')
  // CERT (after Beta 2, which is where its date is compared) is not flagged: only Beta 2 is
  await openDependencies(page, ids.cert)
  await expect(page.getByRole('list', { name: 'Chain advisories' }).locator('li')).toHaveCount(1)
  await page.getByRole('tab', { name: /^Overview/ }).click()
  await expect(page.locator('main')).not.toContainText('Version: Beta 2 is planned for')
})

test('the next release is created from the previous one, in the same version, from its type\'s template', async ({ page }) => {
  const name = `E2E Orion ${stamp}c`
  await signIn(page, 'ver-next-rm@example.com', 'ReleaseManager')
  // CERT's default template: two gates
  const tpl = await page.evaluate(async n => {
    const json = { 'Content-Type': 'application/json' }
    const t = await (await fetch('/api/v1/templates', { method: 'POST', headers: json, body: JSON.stringify({ name: n, defaultRiskTier: 'High',
      gates: [{ gateName: 'Ver Freeze', gateClass: 'Standard', offsetDays: 5, requiredBeforeStatus: 'Gated' }, { gateName: 'Ver CAB', gateClass: 'Compliance', offsetDays: 1, requiredBeforeStatus: 'Executing' }], steps: [], schedule: [] }) })).json() as { id: string; version: number }
    await fetch(`/api/v1/templates/${t.id}:approve`, { method: 'POST', headers: { ...json, 'If-Match': String(t.version) }, body: '{}' })
    const types = await (await fetch('/api/v1/release-types')).json() as { id: string; name: string; version: number; defaultTemplate: { id: string } | null }[]
    const cert = types.find(x => x.name === 'CERT')!
    if (!cert.defaultTemplate) await fetch(`/api/v1/release-types/${cert.id}`, { method: 'PATCH', headers: { ...json, 'If-Match': String(cert.version) }, body: JSON.stringify({ defaultTemplateId: t.id }) })
    return cert.defaultTemplate ? 'existing' : 'ours'
  }, `E2E ver template ${stamp}`)
  expect(['ours', 'existing']).toContain(tpl)

  // Beta 1 on its own, then a version started from the release page
  const beta1 = await page.evaluate(async ([title, date]) => {
    const types = await (await fetch('/api/v1/release-types')).json() as { id: string; name: string }[]
    const r = await fetch('/api/v1/trains', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ title, targetReleaseDate: date, riskTier: 'Low', releaseTypeId: types.find(x => x.name === 'Beta')!.id, betaNumber: 1 }) })
    return (await r.json() as { id: string }).id
  }, [`${name} Beta 1`, day(3)] as const)
  await openDependencies(page, beta1)
  const panel = page.locator('section[aria-label=Version]')
  await expect(panel).toContainText('This release is not in a version')
  await expect(panel.getByRole('button', { name: 'Create version and add this release' })).toBeDisabled()
  await expect(panel).toContainText('Enter the new version’s name.')
  await panel.getByLabel('Or start a new version with this release').fill(name)
  await panel.getByRole('button', { name: 'Create version and add this release' }).click()
  await expect(chain(page, name).first()).toContainText('Beta 1')
  await expect(page.getByTestId('announcer')).toContainText(`Created ${name} and added this release`)

  // Create next release: Beta 2 is offered first, the title and date are prefilled, the date can be changed
  await panel.getByRole('button', { name: 'Create next release…' }).click()
  const form = panel.getByRole('group', { name: 'Create next release' })
  await expect(form.getByLabel('Next release')).toHaveValue(/\|2$/)
  await expect(form.getByLabel('Title')).toHaveValue(`${name} Beta 2`)
  await expect(form.getByLabel('Target date (UTC calendar date)')).toHaveValue(day(10))
  await expect(form).toContainText('has no default template, so the release starts blank')
  await form.getByRole('button', { name: 'Create release' }).click()
  await expect(panel).toContainText(`Created ${name} Beta 2`)
  await panel.getByRole('button', { name: 'Open it →' }).click()
  await expect(page.locator('main h1')).toHaveText(`${name} Beta 2`)
  await expect(context(page)).toContainText('Beta 2, 2 of 2')

  // then CERT, picked from the list: it starts from CERT's default template (gates)
  await page.getByRole('tab', { name: /^Dependencies/ }).click()
  await panel.getByRole('button', { name: 'Create next release…' }).click()
  await form.getByLabel('Next release').selectOption({ label: 'CERT' })
  await expect(form.getByLabel('Title')).toHaveValue(`${name} CERT`)
  await expect(form).toContainText(/Starts from the CERT template/)
  await form.getByLabel('Target date (UTC calendar date)').fill(day(30))
  await form.getByRole('button', { name: 'Create release' }).click()
  await expect(panel).toContainText(`Created ${name} CERT`)
  await panel.getByRole('button', { name: 'Open it →' }).click()
  await expect(page.locator('main h1')).toHaveText(`${name} CERT`)
  await expect(context(page)).toContainText('CERT, 3 of 3')
  await expect(page.locator('main')).toContainText(/Ver Freeze|RT Freeze|Code Freeze/)   // the type's template gave the gates
})

test('the chain is on the calendar and the overview, compactly, and the existing screens keep working', async ({ page }) => {
  const name = `E2E Orion ${stamp}d`
  await signIn(page, 'ver-screens-rm@example.com', 'ReleaseManager')
  const ids = await makeChain(page, name)
  await openPage(page, '/calendar')
  // the Timeline tab lists every release of the period (the upcoming list shows only the eight soonest, which other specs' releases can fill)
  await page.getByRole('tab', { name: 'Timeline' }).click()
  const label = page.getByRole('rowheader').filter({ hasText: `${name} Beta 2` })
  await expect(label).toContainText(`${name} Beta 2`)

  await primaryNav(page).getByRole('link', { name: 'Overview' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Overview' })).toBeVisible()
  const activity = page.getByRole('table', { name: 'Release activity' })
  const arow = activity.locator('tbody tr', { hasText: `${name} CERT` })
  await expect(arow).toContainText(`${name} · CERT, 3 of 3`)
  await expect(arow.getByRole('list', { name: new RegExp(`^${name}:`) })).toBeVisible()
  expect(ids.version).toBeTruthy()
})

test('taking a release out of its version asks first; cancelling changes nothing', async ({ page }) => {
  const name = `E2E Orion ${stamp}e`
  await signIn(page, 'ver-out-rm@example.com', 'ReleaseManager')
  const ids = await makeChain(page, name)
  await openDependencies(page, ids.b2)
  const panel = page.locator('section[aria-label=Version]')
  await panel.getByRole('button', { name: 'Take out of version…' }).click()
  const dialog = page.getByRole('dialog', { name: new RegExp(`^Take .* out of ${name}\\?`) })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(chain(page, name).first().locator('li')).toHaveCount(3)
  await panel.getByRole('button', { name: 'Take out of version…' }).click()
  await dialog.getByRole('button', { name: 'Take out of version' }).click()
  await expect(panel).toContainText('This release is not in a version')
  await expect(context(page).getByText('None')).toBeVisible()
  // the others carry on without it
  await openDependencies(page, ids.b1)
  await expect(chain(page, name).first().locator('li')).toHaveCount(2)
})

test('a Viewer reads the chain and cannot change it', async ({ page }) => {
  const name = `E2E Orion ${stamp}f`
  await signIn(page, 'ver-make-rm@example.com', 'ReleaseManager')
  const ids = await makeChain(page, name)
  await page.getByRole('button', { name: /account and appearance/ }).click()
  await page.getByRole('button', { name: 'Sign out', exact: true }).click()
  await signIn(page, 'ver-viewer@example.com', 'Viewer')
  await openDependencies(page, ids.b2)
  await expect(chain(page, name).first().locator('li')).toHaveCount(3)
  const panel = page.locator('section[aria-label=Version]')
  await expect(panel.getByRole('button', { name: /Create next release|Take out of version/ })).toHaveCount(0)
})

test('Teams & settings lists versions with their chains; a planner adds, renames and archives one behind a confirmation', async ({ page }) => {
  const name = `E2E Vers ${stamp}`, renamed = `E2E Vers ${stamp} renamed`
  await signIn(page, 'ver-admin-rm@example.com', 'ReleaseManager')
  await makeChain(page, `${name} chain`)
  await openPage(page, '/admin?tab=versions')
  await expect(page.getByRole('tab', { name: 'Versions' })).toHaveAttribute('aria-selected', 'true')
  await expect(versionRow(page, `${name} chain`)).toContainText('Beta 1 → Beta 2 → CERT')
  await expect(versionRow(page, `${name} chain`)).toContainText('Active')

  const add = page.getByRole('group', { name: 'Add version' })
  await expect(add.getByRole('button', { name: 'Add version' })).toBeDisabled()
  await expect(add).toContainText('Enter a name for the new version.')
  await add.getByLabel('Name').fill(name)
  await add.getByRole('button', { name: 'Add version' }).click()
  await expect(versionRow(page, name).first()).toContainText('No releases yet')
  await expect(page.getByTestId('announcer')).toContainText(`Version ${name} added`)
  await add.getByLabel('Name').fill(name.toUpperCase())
  await add.getByRole('button', { name: 'Add version' }).click()
  await expect(add.getByRole('alert')).toContainText('exists')

  await page.getByLabel('Name').first().fill(renamed)
  await page.getByRole('button', { name: 'Save version' }).click()
  await expect(versionRow(page, renamed)).toBeVisible()

  await page.getByRole('button', { name: 'Archive', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: `Archive ${renamed}?` })
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(versionRow(page, renamed)).toContainText('Active')
  await page.getByRole('button', { name: 'Archive', exact: true }).click()
  await dialog.getByRole('button', { name: 'Archive version' }).click()
  await expect(versionRow(page, renamed)).toContainText('Archived')
  await page.getByRole('button', { name: 'Restore' }).click()
  await expect(versionRow(page, renamed)).toContainText('Active')
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} mode`, () => {
    test.use({ colorScheme: scheme })
    test(`the Versions tab, a release's chain with an advisory and the create-next form are axe clean (${scheme})`, async ({ page }) => {
      const name = `E2E Orion ${stamp}x${scheme}`
      await signIn(page, `ver-a11y-${scheme}@example.com`, 'ReleaseManager')
      const ids = await makeChain(page, name)
      await openPage(page, '/admin?tab=versions')
      await expect(versionsTable(page)).toBeVisible()
      await scan(page, `Versions (${scheme})`)
      await versionRow(page, name).click()
      await expect(page.getByRole('button', { name: 'Archive', exact: true })).toBeVisible()
      await scan(page, `Versions, one selected (${scheme})`)
      await page.getByRole('button', { name: 'Archive', exact: true }).click()
      await expect(page.getByRole('dialog')).toBeVisible()
      await scan(page, `Archive version dialog (${scheme})`)
      await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()

      // an advisory: Beta 1 slips past Beta 2
      await page.evaluate(async ([id, to]) => {
        const t = await (await fetch(`/api/v1/trains/${id}`)).json() as { version: number }
        await fetch(`/api/v1/trains/${id}`, { method: 'PATCH', headers: { 'Content-Type': 'application/json', 'If-Match': String(t.version) }, body: JSON.stringify({ targetReleaseDate: to }) })
      }, [ids.b1, day(16)] as const)
      await openDependencies(page, ids.b2)
      await expect(page.getByRole('list', { name: 'Chain advisories' })).toBeVisible()
      await scan(page, `Release Dependencies with the chain and an advisory (${scheme})`)
      await page.locator('section[aria-label=Version]').getByRole('button', { name: 'Create next release…' }).click()
      await expect(page.getByRole('group', { name: 'Create next release' })).toBeVisible()
      await scan(page, `Create next release form (${scheme})`)
      await page.getByRole('tab', { name: /^Overview/ }).click()
      await expect(context(page)).toContainText(name)
      await scan(page, `Release page with the compact chain (${scheme})`)
    })
  })
}
