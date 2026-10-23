import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import { signIn } from './support'

// REOS-39: the Connectors admin screen. Credentials are typed into the detail pane and never displayed back; status is a glyph and a word.
// No live tenant is contacted: the only outbound call is "Test connection" against a name that cannot resolve (.invalid), which fails and says why.
const SECRET = 'e2e-Secret-Token-31337'

test('an RTE configures a connector; the secret is never shown back and a failed test says why inline', async ({ page }) => {
  await signIn(page, 'connectors-rte@example.com', 'RTE')
  await page.click('nav[aria-label=Primary] a:has-text("Connectors")')
  await expect(page.locator('main h1')).toHaveText('Connectors')

  const jira = page.locator('table.os-table tr', { hasText: 'Jira' })
  const snow = page.locator('table.os-table tr', { hasText: 'ServiceNow' })
  await expect(jira).toContainText('Not configured')
  await expect(snow).toContainText('Not configured')
  for (const th of await page.locator('table.os-table th').all()) expect((await th.innerText()).trim().length).toBeGreaterThan(0)   // non-empty headers

  await jira.click()
  await expect(jira).toHaveAttribute('aria-current', 'true')
  const detail = page.getByRole('region', { name: 'Jira connector' })
  await expect(detail.getByRole('heading', { name: 'Jira', exact: true })).toBeVisible()
  await expect(detail.getByRole('button', { name: 'Test connection' })).toBeDisabled()
  await expect(detail).toContainText('Save the base URL first.')                    // a disabled action says why

  await detail.locator('label:has-text("Base URL") input').fill('https://e2e-connectors.invalid')
  await detail.getByRole('button', { name: 'Save base URL' }).click()
  await expect(detail).toContainText('Base URL saved.')
  await expect(jira).toContainText('No credentials')

  await detail.locator('label:has-text("Account email") input').fill('rae@example.com')
  await detail.locator('label:has-text("API token") input').fill(SECRET)
  await detail.getByRole('button', { name: 'Save credentials' }).click()
  await expect(detail).toContainText('Credentials saved')
  await expect(jira).toContainText('Stored (ApiToken)')
  await expect(jira).toContainText('Never synced')

  // never displayed back: the fields are empty and the secret is nowhere in the page
  await expect(detail.locator('label:has-text("API token") input')).toHaveValue('')
  await expect(detail.locator('label:has-text("Account email") input')).toHaveValue('')
  expect(await page.content()).not.toContain(SECRET)
  expect(await page.evaluate(async () => await (await fetch('/api/v1/connectors')).text())).not.toContain('e2e-Secret-Token')

  await detail.getByRole('button', { name: 'Test connection' }).click()
  await expect(detail.getByTestId('test-result')).toContainText('✗', { timeout: 30_000 })   // unreachable: the reason is inline (and announced), not a modal, not an alert

  // remove the credentials after a confirmation dialog (it starts on Cancel), the row goes back to "No credentials"
  await detail.getByRole('button', { name: 'Remove credentials' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await dialog.getByRole('button', { name: 'Remove credentials' }).click()
  await expect(jira).toContainText('No credentials')

  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()
  expect(r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')).toEqual([])
})

test('ServiceNow offers OAuth and Basic; an unsafe base URL is refused with the reason', async ({ page }) => {
  await signIn(page, 'connectors-rm@example.com', 'ReleaseManager')
  await page.click('nav[aria-label=Primary] a:has-text("Connectors")')
  await page.locator('table.os-table tr', { hasText: 'ServiceNow' }).click()
  const detail = page.getByRole('region', { name: 'ServiceNow connector' })
  await detail.locator('label:has-text("Base URL") input').fill('https://user:pw@acme.service-now.com')
  await detail.getByRole('button', { name: 'Save base URL' }).click()
  await expect(detail.locator('[role=alert]')).toContainText('credentials')

  await expect(detail.locator('label:has-text("Type") select option')).toHaveText(['OAuth client credentials', 'Basic authentication'])
  await expect(detail.locator('label:has-text("Client id") input')).toBeVisible()
  await detail.locator('label:has-text("Type") select').selectOption('Basic')
  await expect(detail.locator('label:has-text("User name") input')).toBeVisible()
  await expect(detail.locator('label:has-text("Password") input')).toHaveAttribute('type', 'password')
})

test('a Viewer has no Connectors link and, opening the page directly, no actions', async ({ page }) => {
  await signIn(page, 'connectors-viewer@example.com', 'Viewer')
  await expect(page.locator('nav[aria-label=Primary] a:has-text("Connectors")')).toHaveCount(0)
  await page.goto('/connectors')
  await expect(page.locator('main h1')).toHaveText('Connectors')
  await page.locator('table.os-table tr', { hasText: 'Jira' }).click()
  await expect(page.getByRole('region', { name: 'Jira connector' })).toContainText('Only an RTE or Release Manager can change connectors.')
  await expect(page.getByRole('button', { name: 'Save base URL' })).toHaveCount(0)
})
