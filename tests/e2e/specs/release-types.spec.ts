import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { approveAsGovernanceOfficer, openPage, releaseRow, signIn } from './support'

// REOS-116 (RT1, D46, D48): release types. Teams & settings has a Release types tab (table + side panel, archive behind a confirmation); a new release must name its type, a Beta its
// number, and starts from the type's default template; the Releases list, the calendar and analytics filter by type. Adds its own types, template and trains (unique names) to the shared demo database.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now() % 1_000_000
const day = (n: number) => new Date(Date.now() + n * 86_400_000).toISOString().slice(0, 10)

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

/** An Approved template with two gates, through the API as the signed-in Release Manager. */
async function approvedTemplate(page: Page, name: string) {
  const template = await page.evaluate(async n => {
    const json = { 'Content-Type': 'application/json' }
    const t = await (await fetch('/api/v1/templates', { method: 'POST', headers: json, body: JSON.stringify({
      name: n, defaultRiskTier: 'High',
      gates: [{ gateName: 'RT Freeze', gateClass: 'Standard', offsetDays: 5, requiredBeforeStatus: 'Gated' }, { gateName: 'RT CAB', gateClass: 'Compliance', offsetDays: 1, requiredBeforeStatus: 'Executing' }],
      steps: [], schedule: [],
    }) })).json() as { id: string; version: number }
    return t
  }, name)
  await approveAsGovernanceOfficer(page, template)   // not the author, and a Governance Officer for the Compliance gate (RT7)
  return template.id
}

const typesTable = (page: Page) => page.getByRole('table', { name: 'Release types' })
const typeRow = (page: Page, name: string | RegExp) => typesTable(page).locator('tbody tr', { hasText: name })
const drawer = (page: Page) => page.locator('aside[aria-label=Inspector]')

async function openTypes(page: Page) {
  await openPage(page, '/admin?tab=types')
  await expect(page.getByRole('tab', { name: 'Release types' })).toHaveAttribute('aria-selected', 'true')
  await expect(typesTable(page)).toBeVisible()
}

test('the four starting types are listed, in order, with the Beta numbering shown', async ({ page }) => {
  await signIn(page, 'rt-list-rte@example.com', 'RTE')
  await openTypes(page)
  const names = (await typesTable(page).locator('tbody tr td:first-child').allInnerTexts()).map(t => t.split('\n')[0].trim())
  expect(names.slice(0, 4)).toEqual(['Beta', 'CERT', 'PenTest Cert', 'Production'])
  await expect(typeRow(page, /^Beta/)).toContainText('Numbered 1 to 3')
  await expect(typeRow(page, 'Production')).toContainText('Active')
  await expect(page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: 'Teams & settings' })).toBeVisible()
})

test('a planner adds a type, gives it a default template, archives it behind a confirmation and restores it', async ({ page }) => {
  const name = `E2E Hotfix ${stamp}`, tpl = `E2E hotfix template ${stamp}`
  await signIn(page, 'rt-manage-rm@example.com', 'ReleaseManager')
  await approvedTemplate(page, tpl)
  await openTypes(page)

  const add = page.getByRole('group', { name: 'Add release type' })
  await expect(add.getByRole('button', { name: 'Add release type' })).toBeDisabled()
  await expect(add).toContainText('Enter a name for the new type.')
  await add.getByLabel('Name').fill(name)
  await add.getByRole('button', { name: 'Add release type' }).click()
  await expect(typeRow(page, name)).toContainText('Active')
  await expect(page.getByTestId('announcer')).toContainText(`Release type ${name} added`)

  // a name already used (any case) is refused in words
  await add.getByLabel('Name').fill(name.toUpperCase())
  await add.getByRole('button', { name: 'Add release type' }).click()
  await expect(add.getByRole('alert')).toContainText('exists')

  // the detail panel: choose the default template and save
  await page.getByLabel(/^Default template/).first().selectOption({ label: tpl })   // the detail panel's, before the Add panel's
  await page.getByRole('button', { name: 'Save type' }).click()
  await expect(typeRow(page, name)).toContainText(tpl)

  // Archive asks first: Cancel changes nothing, the dialog starts on Cancel
  await page.getByRole('button', { name: 'Archive', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: `Archive ${name}?` })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(typeRow(page, name)).toContainText('Active')
  await page.getByRole('button', { name: 'Archive', exact: true }).click()
  await dialog.getByRole('button', { name: 'Archive type' }).click()
  await expect(typeRow(page, name)).toContainText('Archived')

  // an archived type is not offered to a new release; restoring brings it back
  await page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: 'Releases', exact: true }).click()   // a reload would reopen the last page
  await page.getByRole('main').getByRole('button', { name: 'New train', exact: true }).first().click()
  await expect(drawer(page).getByLabel('Release type', { exact: true }).locator('option', { hasText: name })).toHaveCount(0)
  await drawer(page).getByRole('button', { name: 'Discard' }).click()
  await openTypes(page)
  await typeRow(page, name).click()
  await page.getByRole('button', { name: 'Restore' }).click()
  await expect(typeRow(page, name)).toContainText('Active')
})

test('a Viewer reads the types and cannot change them', async ({ page }) => {
  await signIn(page, 'rt-viewer@example.com', 'Viewer')
  await openTypes(page)
  await expect(page.getByRole('group', { name: 'Add release type' })).toHaveCount(0)
  await typeRow(page, /^CERT/).click()
  await expect(page.getByText('Only an RTE or Release Manager can change release types.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Archive', exact: true })).toHaveCount(0)
})

test('a release cannot be created without a type; a Beta needs its number; the type starts the release from its template', async ({ page }) => {
  const tpl = `E2E type template ${stamp}`, typeName = `E2E Cert-like ${stamp}`, title = `E2E typed ${stamp}`, betaTitle = `E2E beta ${stamp}`
  await signIn(page, 'rt-create-rm@example.com', 'ReleaseManager')
  const tplId = await approvedTemplate(page, tpl)
  await page.evaluate(async ([n, id]) => {
    const json = { 'Content-Type': 'application/json' }
    const made = await (await fetch('/api/v1/release-types', { method: 'POST', headers: json, body: JSON.stringify({ name: n }) })).json() as { id: string; version: number }
    const r = await fetch(`/api/v1/release-types/${made.id}`, { method: 'PATCH', headers: { ...json, 'If-Match': String(made.version) }, body: JSON.stringify({ defaultTemplateId: id }) })
    if (!r.ok) throw new Error(`default template failed: ${r.status}`)
  }, [typeName, tplId] as const)

  await page.goto('/')
  await page.getByRole('main').getByRole('button', { name: 'New train', exact: true }).first().click()
  await expect(drawer(page).locator('h2', { hasText: 'New train' })).toBeVisible()
  await drawer(page).getByLabel('Title', { exact: true }).fill(title)
  await drawer(page).getByLabel('Target release date', { exact: true }).fill(day(60))
  // no type chosen: Create is off, and the line below says why
  await expect(drawer(page).locator('button:text-is("Create train")')).toBeDisabled()
  await expect(drawer(page)).toContainText('Choose a release type.')

  // the type that has a default template says it will start from it
  await drawer(page).getByLabel('Release type', { exact: true }).selectOption({ label: typeName })
  await expect(drawer(page)).toContainText(`starts from the “${tpl}” template`)
  await drawer(page).locator('button:text-is("Create train")').click()
  await expect(page.locator('main h1')).toHaveText(title)
  await expect(page.getByRole('group', { name: 'Release context' })).toContainText(typeName)
  await expect(page.locator('main')).toContainText('RT Freeze')       // the type's template gave the gates
  await expect(page.locator('main')).toContainText('RT CAB')
  await expect(page.locator('main')).toContainText('High risk')       // and its default risk tier

  // a Beta: the number is asked for, and Create waits for it
  await drawer(page).locator('button:text-is("Create another")').click()
  await drawer(page).getByLabel('Title', { exact: true }).fill(betaTitle)
  await drawer(page).getByLabel('Target release date', { exact: true }).fill(day(61))
  await drawer(page).getByLabel('Release type', { exact: true }).selectOption({ label: 'Beta' })
  await expect(drawer(page).locator('button:text-is("Create train")')).toBeDisabled()
  await expect(drawer(page)).toContainText('Choose the number of this Beta release, 1 to 3.')
  await drawer(page).getByLabel('Beta number').selectOption({ label: 'Beta 2' })
  await drawer(page).locator('button:text-is("Create train")').click()
  await expect(page.locator('main h1')).toHaveText(betaTitle)
  await expect(page.getByRole('group', { name: 'Release context' })).toContainText('Beta 2')

  // the Releases list shows "Beta 2" and filters by type (the finished drawer is discarded first: an open New train drawer keeps the list out of the page)
  await drawer(page).getByRole('button', { name: 'Done', exact: true }).click()
  await page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: 'Releases', exact: true }).click()
  await expect(releaseRow(page, betaTitle)).toContainText('Beta 2')
  await page.getByRole('search', { name: 'Filter releases' }).getByLabel('Type').selectOption({ label: 'Beta' })
  await expect(releaseRow(page, betaTitle)).toBeVisible()
  await expect(releaseRow(page, title)).toHaveCount(0)
  await page.getByRole('search', { name: 'Filter releases' }).getByLabel('Type').selectOption({ label: typeName })
  await expect(releaseRow(page, title)).toBeVisible()
  await expect(releaseRow(page, betaTitle)).toHaveCount(0)
})

test('the calendar and analytics filter by release type', async ({ page }) => {
  await signIn(page, 'rt-filters-rte@example.com', 'RTE')
  await openPage(page, '/calendar')
  const calType = page.getByRole('group', { name: 'Filters' }).getByLabel('Release type')
  await expect(calType).toBeVisible()
  await expect(calType.locator('option')).toContainText(['All release types', 'Beta', 'CERT', 'PenTest Cert', 'Production'])
  await calType.selectOption({ label: 'PenTest Cert' })        // nothing is a PenTest Cert release: the filter says so, in words
  await expect(page.locator('main')).toContainText(/No releases match these filters|No upcoming releases match these filters/)
  await calType.selectOption({ label: 'All release types' })

  await openPage(page, '/analytics')
  const anType = page.getByLabel('Release type')
  await expect(anType).toBeVisible()
  const asked = page.waitForRequest(r => /\/api\/v1\/analytics\/M\d+\?.*releaseTypeId=/.test(r.url()))
  await anType.selectOption({ label: 'Production' })
  await asked
  await expect(page.getByTestId('chart').first()).toBeVisible()
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} mode`, () => {
    test.use({ colorScheme: scheme })
    test(`the Release types tab, its detail panel and the archive dialog are axe clean (${scheme})`, async ({ page }) => {
      await signIn(page, `rt-a11y-${scheme}@example.com`, 'RTE')
      await openTypes(page)
      await scan(page, `Release types (${scheme})`)
      await typeRow(page, /^CERT/).click()
      await expect(page.getByRole('button', { name: 'Archive', exact: true })).toBeVisible()
      await scan(page, `Release types, a type selected (${scheme})`)
      await page.getByRole('button', { name: 'Archive', exact: true }).click()
      await expect(page.getByRole('dialog')).toBeVisible()
      await scan(page, `Archive dialog (${scheme})`)
      await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()
    })

    test(`the New train drawer with a type chosen is axe clean (${scheme})`, async ({ page }) => {
      await signIn(page, `rt-a11y-new-${scheme}@example.com`, 'RTE')
      await page.getByRole('main').getByRole('button', { name: 'New train', exact: true }).first().click()
      await drawer(page).getByLabel('Release type', { exact: true }).selectOption({ label: 'Beta' })
      await expect(drawer(page).getByLabel('Beta number')).toBeVisible()
      await scan(page, `New train with a Beta type (${scheme})`)
      await drawer(page).getByRole('button', { name: 'Discard' }).click()
    })
  })
}
