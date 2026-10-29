import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { primaryNav, signIn } from './support'

// REOS-156: the Environments screen. An RTE adds, archives and restores an environment, chooses its control webhook and creates, then rotates, its signing key (shown once, never
// stored or read back); a stale write says so and reloads; a Viewer reads only. Adds its own uniquely named rows to the shared demo database.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad.map(v => `${v.id} (${v.impact}): ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(' | ')}`), `axe violations on ${where}`).toEqual([])
}
async function json(page: Page, method: string, url: string, body?: unknown, ifMatch?: number) {
  return page.evaluate(async ([m, u, b, v]) => {
    const headers: Record<string, string> = { 'Content-Type': 'application/json' }
    if (v !== undefined) headers['If-Match'] = String(v)
    const r = await fetch(u as string, { method: m as string, headers, body: b === undefined ? undefined : JSON.stringify(b) })
    return { status: r.status, body: await r.json().catch(() => null) }
  }, [method, url, body, ifMatch] as const)
}
const openEnvironments = async (page: Page) => {
  await primaryNav(page).getByRole('link', { name: /^Environments$/ }).click()
  await expect(page.locator('main h1')).toHaveText('Environments')
  await expect(page.getByRole('region', { name: 'Environments', exact: true }).getByRole('heading', { name: 'Environments' })).toBeVisible()   // the list panel (an empty workspace shows its empty state, not a table)
}
const row = (page: Page, name: string) => page.getByRole('table', { name: 'Environments' }).locator('tbody tr', { hasText: name })

test('an RTE adds an environment, reads its SLO, archives it after a confirmation and restores it', async ({ page }) => {
  const name = `E2E Env ${Date.now()}`
  await signIn(page, 'env-rte@example.com', 'RTE')
  await openEnvironments(page)

  const add = page.getByRole('form', { name: 'Add environment' })
  await expect(add.getByRole('button', { name: 'Add environment' })).toBeDisabled()
  await expect(add).toContainText('Enter a name for the environment.')           // a disabled action says why
  await add.getByLabel('Name').fill(name)
  await add.getByLabel('Kind').selectOption('Production')
  await add.getByLabel('Region').fill('eu-west-1')
  await add.getByRole('button', { name: 'Add environment' }).click()
  await expect(row(page, name)).toBeVisible()
  await expect(row(page, name)).toContainText('Active')                          // dot + word, never colour alone
  await expect(row(page, name)).toContainText('eu-west-1')
  for (const th of await page.locator('table.os-table th').all()) expect((await th.innerText()).trim().length).toBeGreaterThan(0)   // non-empty headers

  const detail = page.getByRole('region', { name: `${name} environment` })       // the new row is selected
  await expect(detail.getByRole('heading', { name, exact: true })).toBeVisible()
  await expect(detail).toContainText('Service level objectives')
  await expect(detail).toContainText('No target')                                // no SLO is set on a new environment; the figure is still a word
  await expect(detail.getByRole('button', { name: 'Create signing key' })).toBeDisabled()
  await expect(detail).toContainText(`Choose a control webhook for ${name} first.`)

  // a duplicate is refused before it is sent, with the reason
  await add.getByLabel('Name').fill(name.toUpperCase())
  await expect(add.getByRole('button', { name: 'Add environment' })).toBeDisabled()
  await expect(add).toContainText('exists')
  await add.getByLabel('Name').fill('')

  // the range changes the SLO window
  await detail.getByLabel('Range').selectOption('7')
  await expect(detail.getByRole('list', { name: 'SLO by metric' })).toBeVisible()
  await expect(detail.locator('.os-prop', { hasText: 'From' })).toContainText('UTC')   // times are UTC-labelled

  await scan(page, 'Environments with a selection')

  await detail.getByRole('button', { name: 'Archive', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()     // the dialog starts on Cancel
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
  await expect(row(page, name)).toContainText('Active')
  await detail.getByRole('button', { name: 'Archive', exact: true }).click()
  await dialog.getByRole('button', { name: 'Archive environment' }).click()
  await expect(row(page, name)).toContainText('Archived')
  await expect(detail.getByRole('button', { name: 'Restore' })).toBeVisible()

  await page.getByRole('combobox', { name: 'State' }).selectOption('active')     // filter: archived rows leave the Active view
  await expect(row(page, name)).toHaveCount(0)
  await page.getByRole('combobox', { name: 'State' }).selectOption('all')
  await row(page, name).click()
  await detail.getByRole('button', { name: 'Restore' }).click()
  await expect(row(page, name)).toContainText('Active')
})

test('the control webhook is chosen, its signing key is shown once and never read back, and rotating asks first', async ({ page }) => {
  const stamp = Date.now()
  const name = `E2E Signed ${stamp}`
  await signIn(page, 'env-signing-rte@example.com', 'RTE')
  const hook = await json(page, 'POST', '/api/v1/sync/webhook-allowlist', { name: `#env-${stamp}`, url: `https://hooks.slack.com/services/T${stamp}/B${stamp}/e2eenvironments`, kind: 'Slack' })
  expect([200, 201], JSON.stringify(hook.body)).toContain(hook.status)
  expect((await json(page, 'POST', '/api/v1/environments', { name, kind: 'Staging' })).status).toBeLessThan(300)
  await openEnvironments(page)
  await row(page, name).click()
  const detail = page.getByRole('region', { name: `${name} environment` })

  await expect(detail.getByRole('button', { name: 'Save webhook' })).toBeDisabled()
  await detail.getByRole('combobox', { name: 'Control webhook' }).selectOption({ label: `#env-${stamp} (hooks.slack.com)` })
  await detail.getByRole('button', { name: 'Save webhook' }).click()
  await expect(row(page, name)).toContainText(`#env-${stamp}`)
  await expect(row(page, name)).toContainText('No key')

  await detail.getByRole('button', { name: 'Create signing key' }).click()      // first key: no confirmation, nothing is replaced
  const secretBox = detail.getByTestId('signing-secret')
  await expect(secretBox).toBeVisible()
  await expect(detail).toContainText('Copy this signing key now')
  const secret = (await secretBox.innerText()).trim()
  expect(secret.length).toBeGreaterThanOrEqual(24)
  await expect(detail.locator('.os-prop', { hasText: 'Signature header' })).toBeVisible()
  await expect(row(page, name)).toContainText('Key set')
  expect(await page.locator('[aria-live]').allInnerTexts().then(t => t.join(' '))).not.toContain(secret)   // never announced
  expect(await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }) + location.href)).not.toContain(secret)
  await scan(page, 'Environments with a signing key shown')

  // never read back: the list and the row say a key is set, not what it is
  const list = await json(page, 'GET', '/api/v1/environments')
  expect(JSON.stringify(list.body)).not.toContain(secret)

  await detail.getByRole('button', { name: 'Done' }).click()                     // dismissed: gone from the page
  await expect(secretBox).toHaveCount(0)
  expect(await page.content()).not.toContain(secret)

  await detail.getByRole('button', { name: 'Rotate signing key' }).click()       // replacing a key asks first
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await expect(dialog).toContainText('stops working at once')
  await dialog.getByRole('button', { name: 'Rotate key' }).click()
  const second = (await secretBox.innerText()).trim()
  expect(second).not.toEqual(secret)
  await page.getByRole('table', { name: 'Environments' }).locator('tbody tr').first().click()   // picking another row drops the secret
  await expect(secretBox).toHaveCount(0)
  expect(await page.content()).not.toContain(second)
})

test('a write on a stale version says someone else changed it, reloads the row, and loses nothing', async ({ page }) => {
  const name = `E2E Stale ${Date.now()}`
  await signIn(page, 'env-stale-rte@example.com', 'RTE')
  const made = await json(page, 'POST', '/api/v1/environments', { name, kind: 'Staging' })
  const env = made.body as { id: string; version: number }
  await openEnvironments(page)
  await row(page, name).click()
  const detail = page.getByRole('region', { name: `${name} environment` })
  await expect(detail.getByRole('button', { name: 'Archive', exact: true })).toBeVisible()
  expect((await json(page, 'PATCH', `/api/v1/environments/${env.id}`, { region: 'ap-south-1' }, env.version)).status).toBe(200)   // someone else edits first
  await detail.getByRole('button', { name: 'Archive', exact: true }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Archive environment' }).click()
  await expect(detail.getByRole('alert')).toContainText(`Someone else changed ${name}`)
  await expect(row(page, name)).toContainText('ap-south-1')                      // reloaded with their change
  await expect(row(page, name)).toContainText('Active')                          // and not archived by the stale click
  await detail.getByRole('button', { name: 'Archive', exact: true }).click()     // trying again works
  await page.getByRole('dialog').getByRole('button', { name: 'Archive environment' }).click()
  await expect(row(page, name)).toContainText('Archived')
})

test('a Viewer reads environments and their SLO but has no way to change them', async ({ page }) => {
  const name = `E2E Viewer ${Date.now()}`
  await signIn(page, 'env-owner@example.com', 'RTE')
  await json(page, 'POST', '/api/v1/environments', { name, kind: 'Staging' })
  await page.getByRole('button', { name: /account and appearance/ }).click()
  await page.getByRole('button', { name: 'Sign out', exact: true }).click()
  await signIn(page, 'env-viewer@example.com', 'Viewer')
  await openEnvironments(page)
  await expect(page.getByText('Read only', { exact: true })).toBeVisible()
  await expect(page.getByRole('form', { name: 'Add environment' })).toHaveCount(0)
  await row(page, name).click()
  const detail = page.getByRole('region', { name: `${name} environment` })
  await expect(detail).toContainText('Service level objectives')
  for (const b of ['Archive', 'Restore', 'Save webhook', 'Create signing key', 'Rotate signing key']) await expect(detail.getByRole('button', { name: b })).toHaveCount(0)
  expect(await page.evaluate(async id => (await fetch(`/api/v1/environments/${id}:archive`, { method: 'POST' })).status, (await json(page, 'GET', '/api/v1/environments')).body.find((e: { name: string }) => e.name === name).id)).toBe(403)
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })
    test('the list, an empty detail and a selected row are axe clean', async ({ page }) => {
      await signIn(page, `env-axe-${scheme}@example.com`, 'RTE')
      await json(page, 'POST', '/api/v1/environments', { name: `E2E Axe ${scheme} ${Date.now()}`, kind: 'Production', sloAvailabilityPct: 99.9, sloLatencyP95Ms: 300, sloErrorRatePct: 1 })
      await openEnvironments(page)
      await expect(page.getByText('Select an environment')).toBeVisible()
      await scan(page, `Environments (${scheme})`)
      await page.getByRole('table', { name: 'Environments' }).locator('tbody tr').last().click()
      await expect(page.getByText('Service level objectives')).toBeVisible()
      await expect(page.getByRole('list', { name: 'SLO by metric' })).toBeVisible()
      await scan(page, `Environments selected (${scheme})`)
    })
  })
}
