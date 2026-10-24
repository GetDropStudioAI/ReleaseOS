import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, signIn } from './support'

// REOS-118 (RT3, D47): clients. Teams & settings has a Clients tab (table + side panel, archive behind a confirmation): a Standard client has no CERT environment, an On-Demand client
// names one of the workspace's. Clients can be imported from a CSV on Imports & exports. Adds its own environments and clients (unique names) to the shared demo database.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now() % 1_000_000

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

/** An environment through the API as the signed-in planner. */
async function environment(page: Page, name: string) {
  await page.evaluate(async n => {
    const r = await fetch('/api/v1/environments', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: n, kind: 'Staging' }) })
    if (!r.ok) throw new Error(`environment failed: ${r.status}`)
  }, name)
}

const table = (page: Page) => page.getByRole('table', { name: 'Clients' })
const row = (page: Page, name: string | RegExp) => table(page).locator('tbody tr', { hasText: name })
const csv = (rows: string[][]) => Buffer.from(rows.map(r => r.join(',')).join('\r\n') + '\r\n')

async function openClients(page: Page) {
  await openPage(page, '/admin?tab=clients')
  await expect(page.getByRole('tab', { name: 'Clients' })).toHaveAttribute('aria-selected', 'true')
  await expect(table(page)).toBeVisible()
}

test('a planner adds a Standard and an On-Demand client; the form says what is missing in words', async ({ page }) => {
  const cert = `E2E CERT ${stamp}`, std = `E2E Acme ${stamp}`, od = `E2E Globex ${stamp}`
  await signIn(page, 'cl-add-rm@example.com', 'ReleaseManager')
  await environment(page, cert)
  await openClients(page)

  const add = page.getByRole('group', { name: 'Add client' })
  await expect(add.getByRole('button', { name: 'Add client' })).toBeDisabled()
  await expect(add).toContainText('Enter a name for the client.')
  await add.getByLabel('Name').fill(std)
  await add.getByRole('button', { name: 'Add client' }).click()
  await expect(row(page, std)).toContainText('Standard')
  await expect(row(page, std)).toContainText('None')                      // no CERT environment
  await expect(page.getByTestId('announcer')).toContainText(`Client ${std} added`)

  // On-Demand: the environment picker appears and Add waits for it
  await add.getByLabel('Name').fill(od)
  await add.getByLabel('Segment').selectOption({ label: 'On-Demand' })
  await expect(add.getByRole('button', { name: 'Add client' })).toBeDisabled()
  await expect(add).toContainText('Choose the CERT environment of this On-Demand client.')
  await add.getByLabel('CERT environment').selectOption({ label: cert })
  await add.getByRole('button', { name: 'Add client' }).click()
  await expect(row(page, od)).toContainText('On-Demand')
  await expect(row(page, od)).toContainText(cert)

  // a name already used (any case) is refused in words
  await add.getByLabel('Name').fill(std.toUpperCase())
  await add.getByRole('button', { name: 'Add client' }).click()
  await expect(add.getByRole('alert')).toContainText('exists')
})

test('changing the segment moves the environment with it; archive asks first and restore brings the client back', async ({ page }) => {
  const cert = `E2E CERT2 ${stamp}`, name = `E2E Initech ${stamp}`
  await signIn(page, 'cl-edit-rte@example.com', 'RTE')
  await environment(page, cert)
  await openClients(page)
  const add = page.getByRole('group', { name: 'Add client' })
  await add.getByLabel('Name').fill(name)
  await add.getByLabel('Segment').selectOption({ label: 'On-Demand' })
  await add.getByLabel('CERT environment').selectOption({ label: cert })
  await add.getByRole('button', { name: 'Add client' }).click()
  await expect(row(page, name)).toContainText(cert)

  // the new client is selected: make it Standard — the environment goes
  const detail = page.getByRole('region', { name: name }).or(page.locator('section', { has: page.getByRole('heading', { name }) })).first()
  await detail.getByLabel('Segment').selectOption({ label: 'Standard' })
  await expect(detail).toContainText('A Standard client has no CERT environment.')
  await detail.getByRole('button', { name: 'Save client' }).click()
  await expect(row(page, name)).toContainText('Standard')
  await expect(row(page, name)).not.toContainText(cert)

  // back to On-Demand: Save waits for an environment
  await detail.getByLabel('Segment').selectOption({ label: 'On-Demand' })
  await expect(detail.getByRole('button', { name: 'Save client' })).toBeDisabled()
  await expect(detail).toContainText('Choose the CERT environment of this On-Demand client.')
  await detail.getByLabel('CERT environment').selectOption({ label: cert })
  await detail.getByRole('button', { name: 'Save client' }).click()
  await expect(row(page, name)).toContainText(cert)

  // Archive asks first: Cancel changes nothing, the dialog starts on Cancel
  await page.getByRole('button', { name: 'Archive', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: `Archive ${name}?` })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(row(page, name)).toContainText('Active')
  await page.getByRole('button', { name: 'Archive', exact: true }).click()
  await dialog.getByRole('button', { name: 'Archive client' }).click()
  await expect(row(page, name)).toContainText('Archived')
  await page.getByRole('button', { name: 'Restore' }).click()
  await expect(row(page, name)).toContainText('Active')
})

test('a Viewer reads the clients and cannot change them', async ({ page }) => {
  await signIn(page, 'cl-viewer@example.com', 'Viewer')
  await openClients(page)
  await expect(page.getByRole('group', { name: 'Add client' })).toHaveCount(0)
  await expect(page.getByRole('link', { name: 'Import clients from a CSV' })).toHaveCount(0)
  const first = table(page).locator('tbody tr:not(:has(.os-table-empty))').first()
  if (await first.count()) {
    await first.click()
    await expect(page.getByText('Only an RTE or Release Manager can change clients.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Archive', exact: true })).toHaveCount(0)
  }
})

test('clients import from a CSV: a bad row blocks the commit with its row and column; a good file commits and the clients are listed', async ({ page }) => {
  const cert = `E2E CERT3 ${stamp}`, a = `E2E Hooli ${stamp}`, b = `E2E Umbrella ${stamp}`
  await signIn(page, 'cl-import-rte@example.com', 'RTE')
  await environment(page, cert)
  await openClients(page)
  await page.getByRole('link', { name: 'Import clients from a CSV' }).click()
  await expect(page).toHaveURL(/\/importexport$/)
  await page.getByRole('group', { name: 'Import kind' }).getByRole('button', { name: 'Clients', exact: true }).click()

  // an On-Demand row with no environment is an error on its column, and nothing can be committed
  await page.locator('input[type=file]').setInputFiles({ name: 'clients-bad.csv', mimeType: 'text/csv', buffer: csv([['Name', 'Segment', 'CertEnvironment'], [a, 'Standard', ''], [b, 'On-Demand', '']]) })
  await expect(page.locator('.import-bar .counts')).toContainText('1 error')
  await expect(page.locator('section[aria-label=Errors]')).toContainText('CertEnvironment')
  await expect(page.locator('section[aria-label=Errors]')).toContainText('needs a CERT environment')
  await expect(page.getByRole('button', { name: /^Commit/ })).toBeDisabled()

  await page.locator('input[type=file]').setInputFiles({ name: 'clients-good.csv', mimeType: 'text/csv', buffer: csv([['Name', 'Segment', 'CertEnvironment'], [a, 'Standard', ''], [b, 'On-Demand', cert]]) })
  await expect(page.locator('.import-bar .counts')).toContainText('2 new')
  await expect(page.locator('.import-bar .counts')).toContainText('0 errors')
  await page.getByRole('button', { name: 'Commit 2 rows' }).click()
  await expect(page.locator('.os-notice', { hasText: 'Committed Clients: 2 new' })).toBeVisible()

  await openClients(page)
  await expect(row(page, a)).toContainText('Standard')
  await expect(row(page, b)).toContainText('On-Demand')
  await expect(row(page, b)).toContainText(cert)
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} mode`, () => {
    test.use({ colorScheme: scheme })
    test(`the Clients tab, its detail panel, the On-Demand form and the archive dialog are axe clean (${scheme})`, async ({ page }) => {
      const cert = `E2E CERT-a11y ${scheme} ${stamp}`, name = `E2E a11y ${scheme} ${stamp}`
      await signIn(page, `cl-a11y-${scheme}@example.com`, 'RTE')
      await environment(page, cert)
      await openClients(page)
      await scan(page, `Clients (${scheme})`)
      const add = page.getByRole('group', { name: 'Add client' })
      await add.getByLabel('Name').fill(name)
      await add.getByLabel('Segment').selectOption({ label: 'On-Demand' })
      await scan(page, `Clients, the On-Demand add form (${scheme})`)
      await add.getByLabel('CERT environment').selectOption({ label: cert })
      await add.getByRole('button', { name: 'Add client' }).click()
      await expect(row(page, name)).toContainText(cert)
      await scan(page, `Clients, a client selected (${scheme})`)
      await page.getByRole('button', { name: 'Archive', exact: true }).click()
      await expect(page.getByRole('dialog')).toBeVisible()
      await scan(page, `Archive dialog (${scheme})`)
      await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()
    })
  })
}
