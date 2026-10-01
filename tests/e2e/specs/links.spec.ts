import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn, trainByTitle } from './support'

// REOS-82: the Links section of the train workspace. Add a ServiceNow change to a product and a Jira issue to the train, read their sync state in words,
// remove both with the inline confirm (Cancel first, then Confirm), and check the section with axe in light and dark. Leaves the shared database as it found it.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe violations on ${where}:\n${bad.map(v => `${v.id}: ${v.help} ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('\n')}`).toEqual([])
}

test('an RTE adds two links, reads their state in words, and removes them with an inline confirm; axe clean in both themes', async ({ page }) => {
  await signIn(page, 'links-rte@example.com', 'RTE')
  const t = await trainByTitle(page, 'Reporting')
  await page.goto(`/trains/${t.id}`)
  const links = page.locator('section[aria-label="Links"]')
  await expect(links.locator('h2')).toHaveText('Links')
  await expect(links).toContainText('No links.')

  // add: ServiceNow change on a product (the key is typed in lower case; the server stores it upper case)
  await links.locator('.section-head button:text-is("Add link")').click()
  const form = links.locator('[role=group][aria-label="Add link"]')
  await expect(form.locator('button:text-is("Add link")')).toBeDisabled()
  await expect(form).toContainText('Enter the Jira key')                                // a disabled action says why
  await form.locator('button.choice:text-is("ServiceNow")').click()
  await expect(form.locator('button.choice:text-is("ServiceNow")')).toHaveAttribute('aria-pressed', 'true')
  await form.locator('label:has-text("Key") input').fill('chg0030777')
  await form.locator('label:has-text("Attach to") select').selectOption({ label: 'Card Portal 2.1.0' })
  for (const scheme of ['light', 'dark'] as const) { await page.emulateMedia({ colorScheme: scheme }); await scan(page, `links form (${scheme})`) }
  await form.locator('button:text-is("Add link")').click()

  const chg = links.locator('[data-testid=link-row]', { hasText: 'CHG0030777' })
  await expect(chg).toContainText('ServiceNow')
  await expect(chg).toContainText('Product · Card Portal 2.1.0')
  await expect(chg).toContainText('○ not yet checked')
  await expect(chg).toContainText('never')

  // add: Jira issue on the train itself; a bad key is refused inline with the reason
  await links.locator('.section-head button:text-is("Add link")').click()
  await expect(form.locator('button.choice:text-is("ServiceNow")')).toHaveAttribute('aria-pressed', 'true')   // the last system and target are kept for the next key
  await form.locator('button.choice:text-is("Jira")').click()
  await form.locator('label:has-text("Attach to") select').selectOption({ label: 'This train' })
  await form.locator('label:has-text("Key") input').fill('not a key')
  await form.locator('button:text-is("Add link")').click()
  await expect(links.locator('[role=alert]')).toContainText('A Jira key is an issue key')
  await form.locator('label:has-text("Key") input').fill('RPT-4242')
  await form.locator('button:text-is("Add link")').click()
  const jira = links.locator('[data-testid=link-row]', { hasText: 'RPT-4242' })
  await expect(jira).toContainText('Train')
  await expect(links.locator('[role=alert]')).toHaveCount(0)

  // SEC-C: the key is a link only when built from the configured Jira base URL (another spec may have saved one); never anything else
  const jiraBase = await page.evaluate(async () => ((await (await fetch('/api/v1/connectors')).json()) as { source: string; baseUrl: string; isEnabled: boolean }[]).find(c => c.source === 'Jira'))
  const anchor = jira.locator('a')
  if (jiraBase?.isEnabled && jiraBase.baseUrl.startsWith('https://')) await expect(anchor).toHaveAttribute('href', `${jiraBase.baseUrl.replace(/\/+$/, '')}/browse/RPT-4242`)
  else await expect(anchor).toHaveCount(0)
  await expect(chg.locator('a[href^="javascript"], a[href^="data"]')).toHaveCount(0)

  for (const scheme of ['light', 'dark'] as const) { await page.emulateMedia({ colorScheme: scheme }); await scan(page, `links table (${scheme})`) }

  // remove: Cancel leaves it, Confirm removes it
  await jira.locator('button[aria-label="Remove RPT-4242"]').click()
  const confirm = jira.locator('[role=group][aria-label="Confirm remove RPT-4242"]')
  await expect(confirm).toContainText('Sync health stops watching RPT-4242 for this train')
  await confirm.locator('button:text-is("Cancel")').click()
  await expect(jira).toBeVisible()
  await jira.locator('button[aria-label="Remove RPT-4242"]').click()
  await jira.locator('button:text-is("Confirm remove")').click()
  await expect(jira).toHaveCount(0)
  await chg.locator('button[aria-label="Remove CHG0030777"]').click()
  await chg.locator('button:text-is("Confirm remove")').click()
  await expect(links).toContainText('No links.')
  expect(await page.evaluate(async id => (await (await fetch(`/api/v1/trains/${id}/links`)).json()).length, t.id)).toBe(0)
})

test('a Viewer sees the Links section without Add or Remove', async ({ page }) => {
  await signIn(page, 'links-viewer@example.com', 'Viewer')
  const t = await trainByTitle(page, 'Reporting')
  await page.goto(`/trains/${t.id}`)
  const links = page.locator('section[aria-label="Links"]')
  await expect(links.locator('h2')).toHaveText('Links')
  await expect(links.locator('button')).toHaveCount(0)
})
