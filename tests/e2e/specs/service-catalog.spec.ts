import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, primaryNav, signIn } from './support'

// REOS-155 (B8): the Service catalog screen replaces the placeholder. A searchable table with a side panel: add, edit and archive (archive behind a confirmation), If-Match/409 with
// Use theirs / Overwrite, loading / empty / error states with Retry, read-only for a Viewer. Adds its own services (unique keys) to the shared demo database.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now() % 1_000_000

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

const table = (page: Page) => page.getByRole('table', { name: 'Services' })
const row = (page: Page, text: string | RegExp) => table(page).locator('tbody tr:not(:has(.os-table-empty))', { hasText: text })
const add = (page: Page) => page.getByRole('group', { name: 'Add service' })
const edit = (page: Page) => page.getByRole('group', { name: 'Edit service' })

async function openCatalog(page: Page) {
  await openPage(page, '/services')
  await expect(page.getByRole('heading', { level: 1, name: 'Service catalog' })).toBeVisible()
  await expect(table(page).or(page.getByText('No services yet'))).toBeVisible()   // a fresh database has no services, so no table
}

/** A service through the API as the signed-in planner; returns its row. */
async function service(page: Page, key: string, name: string, extra: Record<string, string> = {}) {
  return page.evaluate(async ([k, n, x]) => {
    const r = await fetch('/api/v1/services', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ serviceKey: k, name: n, ...x }) })
    if (!r.ok) throw new Error(`service failed: ${r.status} ${await r.text()}`)
    return await r.json() as { id: string; version: number }
  }, [key, name, extra] as const)
}

async function team(page: Page, handle: string, name: string) {
  return page.evaluate(async ([h, n]) => {
    const r = await fetch('/api/v1/teams', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ handle: h, name: n }) })
    if (!r.ok) throw new Error(`team failed: ${r.status} ${await r.text()}`)
  }, [handle, name] as const)
}

test('the sidebar opens the real Service catalog, not the placeholder', async ({ page }) => {
  await signIn(page, 'sc-nav-rte@example.com', 'RTE')
  await primaryNav(page).getByRole('link', { name: 'Service catalog' }).click()
  await expect(page).toHaveURL(/\/services$/)
  await expect(table(page).or(page.getByText('No services yet'))).toBeVisible()
  await expect(page.getByText('This screen is not built yet')).toHaveCount(0)
  await expect(page).toHaveTitle('Service catalog · ReleaseOS')
})

test('a planner adds a service, searches for it, edits it and archives it behind a confirmation; restore needs none', async ({ page }) => {
  const key = `e2e-pay-${stamp}`, name = `E2E Payments ${stamp}`, handle = `e2esc${stamp}`, teamName = `E2E Team ${stamp}`
  await signIn(page, 'sc-add-rm@example.com', 'ReleaseManager')
  await team(page, handle, teamName)
  await openCatalog(page)

  await page.getByRole('button', { name: 'Add service' }).first().click()
  await expect(add(page).getByRole('button', { name: 'Add service' })).toBeDisabled()
  await expect(add(page)).toContainText('Enter a key for the service.')
  await add(page).getByLabel('Key').fill('bad key')
  await expect(add(page)).toContainText('letters, digits')
  await add(page).getByLabel('Key').fill(key)
  await expect(add(page)).toContainText('Enter a name for the service.')
  await add(page).getByLabel('Name', { exact: true }).fill(name)
  await add(page).getByLabel('Owner team').selectOption({ label: `${teamName} (@${handle})` })
  await add(page).getByLabel('Repository').fill('git.example/e2e/pay')
  await add(page).getByRole('button', { name: 'Add service' }).click()
  await expect(page.getByTestId('announcer')).toContainText(`Service ${name} added`)
  await expect(row(page, key)).toContainText(name)
  await expect(row(page, key)).toContainText(teamName)
  await expect(row(page, key)).toContainText('Active')

  // the new service is open for editing; a second service with the same key (any case) is refused in words
  await page.getByRole('button', { name: 'Add service' }).first().click()
  await add(page).getByLabel('Key').fill(key.toUpperCase())
  await add(page).getByLabel('Name', { exact: true }).fill('Duplicate')
  await add(page).getByRole('button', { name: 'Add service' }).click()
  await expect(add(page).getByRole('alert')).toContainText('exists')

  // search narrows the table by key, name or team; nothing matching says so
  const search = page.getByRole('searchbox', { name: 'Search services' })
  await search.fill(`@${handle}`)
  await expect(table(page).locator('tbody tr')).toHaveCount(1)
  await search.fill('zzz-no-such-service')
  await expect(table(page)).toContainText('No services match “zzz-no-such-service”.')
  await search.fill(key.toUpperCase())
  await expect(row(page, key)).toBeVisible()

  // edit: Save waits for a change; clearing the team and repository sends empty strings
  await row(page, key).click()
  await expect(edit(page).getByRole('button', { name: 'Save service' })).toBeDisabled()
  await expect(edit(page)).toContainText('Change something first.')
  await edit(page).getByLabel('Name', { exact: true }).fill(`${name} v2`)
  await edit(page).getByLabel('Owner team').selectOption({ label: 'No owner team' })
  await edit(page).getByLabel('Repository').fill('')
  await edit(page).getByRole('button', { name: 'Save service' }).click()
  await expect(page.getByTestId('announcer')).toContainText(`${name} v2 saved`)
  await expect(row(page, key)).toContainText(`${name} v2`)
  await expect(row(page, key)).toContainText('No owner team')

  // Archive asks first: the dialog starts on Cancel and Cancel changes nothing
  await edit(page).getByRole('button', { name: 'Archive', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: `Archive ${name} v2?` })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(row(page, key)).toContainText('Active')
  await edit(page).getByRole('button', { name: 'Archive', exact: true }).click()
  await dialog.getByRole('button', { name: 'Archive service' }).click()
  await expect(page.getByTestId('announcer')).toContainText(`${name} v2 archived`)
  await expect(row(page, key)).toHaveCount(0)   // it leaves the Active list; its panel stays open, now saying Archived
  await expect(edit(page)).toBeVisible()
  await page.getByLabel('State').selectOption({ label: 'Archived services' })
  await expect(row(page, key)).toContainText('Archived')
  await edit(page).getByRole('button', { name: 'Restore' }).click()
  await expect(page.getByTestId('announcer')).toContainText(`${name} v2 restored`)
  await page.getByLabel('State').selectOption({ label: 'Active services' })
  await expect(row(page, key)).toContainText('Active')
})

test('a service changed by someone else: Save offers Use theirs and Overwrite, and the draft is kept', async ({ page }) => {
  const key = `e2e-conf-${stamp}`, name = `E2E Conflict ${stamp}`
  await signIn(page, 'sc-conflict-rte@example.com', 'RTE')
  const made = await service(page, key, name)
  await openCatalog(page)
  await row(page, key).click()
  await edit(page).getByLabel('Project code').fill('MINE')

  // someone else saves first
  await page.evaluate(async ([id, v]) => {
    const r = await fetch(`/api/v1/services/${id}`, { method: 'PATCH', headers: { 'Content-Type': 'application/json', 'If-Match': String(v) }, body: JSON.stringify({ name: 'Theirs' }) })
    if (!r.ok) throw new Error(`patch failed ${r.status}`)
  }, [made.id, made.version] as const)

  await edit(page).getByRole('button', { name: 'Save service' }).click()
  const notice = page.getByText('Someone else changed this first')
  await expect(notice).toBeVisible()
  await expect(edit(page).getByLabel('Project code')).toHaveValue('MINE')   // the draft is still here
  await page.getByRole('button', { name: 'Overwrite' }).click()
  await expect(row(page, key)).toContainText(name)                           // overwritten with the draft's name, which is the original
  await expect(page.getByTestId('announcer')).toContainText(`${name} saved`)

  // again, and this time take theirs
  const row2 = await page.evaluate(async id => (await (await fetch(`/api/v1/services/${id}`)).json()) as { version: number }, made.id)
  await edit(page).getByLabel('Project code').fill('MINE2')
  await page.evaluate(async ([id, v]) => {
    await fetch(`/api/v1/services/${id}`, { method: 'PATCH', headers: { 'Content-Type': 'application/json', 'If-Match': String(v) }, body: JSON.stringify({ name: 'Theirs again' }) })
  }, [made.id, row2.version] as const)
  await edit(page).getByRole('button', { name: 'Save service' }).click()
  await expect(notice).toBeVisible()
  await page.getByRole('button', { name: 'Use theirs' }).click()
  await expect(edit(page).getByLabel('Name', { exact: true })).toHaveValue('Theirs again')
  await expect(edit(page).getByLabel('Project code')).toHaveValue('MINE')
  await expect(notice).toHaveCount(0)
})

test('a Viewer reads the catalog and cannot change it', async ({ page }) => {
  await signIn(page, 'sc-viewer@example.com', 'Viewer')
  await openCatalog(page)
  await expect(page.getByText('Only an RTE or Release Manager can add, edit or archive services.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Add service' })).toHaveCount(0)
  await expect(page.getByRole('link', { name: /Import services/ })).toHaveCount(0)
  await page.getByRole('searchbox', { name: 'Search services' }).fill('')
  const first = table(page).locator('tbody tr:not(:has(.os-table-empty))').first()
  if (await first.count()) {
    await first.click()
    await expect(page.getByRole('button', { name: 'Archive', exact: true })).toHaveCount(0)
    await expect(page.getByRole('button', { name: 'Save service' })).toHaveCount(0)
    await expect(page.getByLabel('Name', { exact: true })).toHaveCount(0)
  }
})

test('loading, error with Retry, and empty states', async ({ page }) => {
  await signIn(page, 'sc-states-rte@example.com', 'RTE')
  // loading: the request is held until released
  let release: () => void = () => undefined
  const gate = new Promise<void>(r => { release = r })
  await page.route('**/api/v1/services?**', async route => { await gate; await route.continue() })
  await page.goto('/services')
  await expect(page.getByText('Loading the service catalog…')).toBeVisible()
  release()
  await expect(table(page)).toBeVisible()
  await page.unroute('**/api/v1/services?**')

  // error: the list fails once; the message and Retry are shown, Retry loads it
  let failing = true
  await page.route('**/api/v1/services?**', route => (failing ? route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'The catalog is down.' }) }) : route.continue()))
  await page.goto('/services')
  const err = page.locator('.os-notice').filter({ hasText: 'could not be loaded' })
  await expect(err).toContainText('The catalog is down.')
  failing = false
  await err.getByRole('button', { name: 'Retry' }).click()
  await expect(table(page)).toBeVisible()
  await page.unroute('**/api/v1/services?**')

  // empty: no services at all offers to add one
  await page.route('**/api/v1/services?**', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }))
  await page.goto('/services')
  await expect(page.getByText('No services yet')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Add service' })).toHaveCount(2)   // heading action and the empty state's
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} mode`, () => {
    test.use({ colorScheme: scheme })
    test(`the Service catalog, its detail, the add form, the conflict, the archive dialog and its states are axe clean (${scheme})`, async ({ page }) => {
      const key = `e2e-a11y-${scheme}-${stamp}`, name = `E2E a11y ${scheme} ${stamp}`
      await signIn(page, `sc-a11y-${scheme}@example.com`, 'RTE')
      const made = await service(page, key, name, { repository: 'git.example/a11y' })
      await openCatalog(page)
      await scan(page, `Service catalog (${scheme})`)
      await page.getByRole('button', { name: 'Add service' }).first().click()
      await add(page).getByLabel('Key').fill('x y')
      await scan(page, `Service catalog, the add form with a problem (${scheme})`)
      await page.getByRole('searchbox', { name: 'Search services' }).fill(key)
      await row(page, key).click()
      await scan(page, `Service catalog, a service selected (${scheme})`)
      await edit(page).getByLabel('Name', { exact: true }).fill(`${name} edited`)
      await page.evaluate(async ([id, v]) => { await fetch(`/api/v1/services/${id}`, { method: 'PATCH', headers: { 'Content-Type': 'application/json', 'If-Match': String(v) }, body: JSON.stringify({ projectCode: 'X' }) }) }, [made.id, made.version] as const)
      await edit(page).getByRole('button', { name: 'Save service' }).click()
      await expect(page.getByText('Someone else changed this first')).toBeVisible()
      await scan(page, `Service catalog, a conflict (${scheme})`)
      await page.getByRole('button', { name: 'Use theirs' }).click()
      await edit(page).getByRole('button', { name: 'Archive', exact: true }).click()
      await expect(page.getByRole('dialog')).toBeVisible()
      await scan(page, `Service catalog, the archive dialog (${scheme})`)
      await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()

      await page.route('**/api/v1/services?**', route => route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'The catalog is down.' }) }))
      await page.goto('/services')
      await expect(page.getByRole('button', { name: 'Retry' })).toBeVisible()
      await scan(page, `Service catalog, the error state (${scheme})`)
      await page.unroute('**/api/v1/services?**')
      await page.route('**/api/v1/services?**', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }))
      await page.goto('/services')
      await expect(page.getByText('No services yet')).toBeVisible()
      await scan(page, `Service catalog, the empty state (${scheme})`)
    })
  })
}
