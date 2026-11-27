import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, primaryNav, signIn } from './support'

// REOS-167 (D61): email delivery. The e2e host has email OFF (the default), which is the state worth proving in the browser: the settings page says so in words and still saves choices, the Email tab of
// Integrations (RTE and Release Manager) says so and offers no way to send, the suppression list can be managed (add, remove after a confirmation dialog), the bounce-events secret is shown once, a Viewer
// reaches their own settings but not the admin tab, no checkbox or switch anywhere, axe in both themes. No mail is sent: there is no provider in this host.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now() % 1_000_000

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

const openSettings = async (page: Page) => {
  await openPage(page, '/notification-settings')
  await expect(page.getByRole('heading', { level: 1, name: 'Notification settings' })).toBeVisible()
  await expect(page.getByRole('table', { name: 'Notification channels' })).toBeVisible()
}
const tab = (page: Page, name: string | RegExp) => page.getByRole('tablist', { name: 'Integrations sections' }).getByRole('tab', { name })
const openEmail = async (page: Page) => {
  await openPage(page, '/integrations')
  await tab(page, 'Email').click()
  await expect(page.getByRole('heading', { level: 2, name: 'Delivery' })).toBeVisible()
}

test('notification settings: the inbox is always on, email and the team webhook are selects that save at once and survive a reload, and the page says email is off', async ({ page }) => {
  await signIn(page, `email-prefs-rte-${stamp}@example.com`, 'RTE')
  await openSettings(page)
  await expect(page).toHaveTitle('Notification settings · ReleaseOS')
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toContainText('Notification settings')
  await expect(page.getByText('Email delivery is off').first()).toBeVisible()
  const table = page.getByRole('table', { name: 'Notification channels' })
  await expect(table.getByRole('row')).toHaveCount(5)   // the header and the four kinds
  await expect(table.getByText('Always on')).toHaveCount(4)   // words beside the dot, never colour alone
  const email = page.getByRole('combobox', { name: 'Email for Approval requested' })
  await expect(email).toHaveValue('Immediate')
  await expect(page.getByRole('combobox', { name: 'Team webhook for Approval requested' })).toHaveValue('Off')
  // the webhook cannot be summarised daily; email can
  await expect(page.getByRole('combobox', { name: 'Team webhook for Approval requested' }).locator('option')).toHaveText(['Off', 'Immediately'])
  await expect(email.locator('option')).toHaveText(['Off', 'Immediately', 'In the daily digest'])

  await email.selectOption('Daily')
  await expect(page.getByRole('status').filter({ hasText: 'Approval requested, email: in the daily digest. Saved.' })).toBeVisible()
  await page.getByRole('combobox', { name: 'Email for Escalation, level 2' }).selectOption('Off')
  await expect(page.getByRole('status').filter({ hasText: 'Escalation, level 2, email: off. Saved.' })).toBeVisible()
  await page.reload()
  await expect(page.getByRole('combobox', { name: 'Email for Approval requested' })).toHaveValue('Daily')
  await expect(page.getByRole('combobox', { name: 'Email for Escalation, level 2' })).toHaveValue('Off')
  await expect(page.getByRole('combobox', { name: 'Email for Decision made' })).toHaveValue('Immediate')
  await expect(page.locator('input[type=checkbox], input[type=radio], [role=switch]')).toHaveCount(0)
  await expect(page.getByText(/sent once a day, after 07:00 UTC/)).toBeVisible()
  await expect(page.getByText(/never carries ticket text/)).toBeVisible()
  // put it back so the shared database ends as it began
  await page.getByRole('combobox', { name: 'Email for Approval requested' }).selectOption('Immediate')
  await page.getByRole('combobox', { name: 'Email for Escalation, level 2' }).selectOption('Immediate')
  await expect(page.getByRole('status').filter({ hasText: 'Escalation, level 2, email: immediately. Saved.' })).toBeVisible()
})

test('a stale settings screen is told, in words, that the choice changed elsewhere, and shows it as it is now', async ({ page }) => {
  await signIn(page, `email-stale-${stamp}@example.com`, 'ReleaseManager')
  await openSettings(page)
  // another tab changes the choice behind this screen's back
  await page.evaluate(async () => { await fetch('/api/v1/me/notification-preferences', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ category: 'DecisionMade', channel: 'Email', mode: 'Off', version: 0 }) }) })
  await page.getByRole('combobox', { name: 'Email for Decision made' }).selectOption('Daily')
  await expect(page.getByText('Another tab or device changed this setting')).toBeVisible()
  await expect(page.getByRole('combobox', { name: 'Email for Decision made' })).toHaveValue('Off')
})

test('a Viewer reaches their own notification settings from the Inbox, and has no Integrations page', async ({ page }) => {
  const calls: string[] = []
  page.on('request', r => { const p = new URL(r.url()).pathname; if (p.startsWith('/api/v1/email')) calls.push(`${r.method()} ${p}`) })
  await signIn(page, `email-viewer-${stamp}@example.com`, 'Viewer')
  await page.goto('/inbox')
  await page.getByRole('button', { name: 'Notification settings' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Notification settings' })).toBeVisible()
  await expect(page).toHaveURL(/\/notification-settings$/)
  await page.getByRole('combobox', { name: 'Email for Decision made' }).selectOption('Off')
  await expect(page.getByRole('status').filter({ hasText: 'Decision made, email: off. Saved.' })).toBeVisible()
  await page.goto('/integrations')
  await expect(page.getByText('You cannot manage integrations')).toBeVisible()
  await expect(page.getByRole('tablist')).toHaveCount(0)
  await page.waitForTimeout(400)
  expect(calls, 'no admin email call for a Viewer').toEqual([])
})

test('the Email tab says email is off, cannot send, and offers no credential field when none is needed', async ({ page }) => {
  await signIn(page, `email-admin-${stamp}@example.com`, 'RTE')
  await openEmail(page)
  await expect(page.getByText('Email delivery is off. Set Notifications:Smtp:Enabled')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Send test email to me' })).toBeDisabled()
  await expect(page.getByText('Email delivery is off, so a test cannot be sent.')).toBeVisible()
  const state = page.getByRole('group', { name: 'Waiting to send' })
  await expect(state).toContainText('0')
  await expect(page.getByText('Not needed')).toBeVisible()   // the SMTP relay in this host has no user name, so there is no credential to store
  await expect(page.getByRole('button', { name: 'Save credential' })).toHaveCount(0)
  await expect(page.getByText('Nothing was queued')).toBeVisible()
  await expect(page.locator('input[type=checkbox], input[type=radio], [role=switch]')).toHaveCount(0)
})

test('the suppression list: add an address, see why and by whom, remove it after a confirmation dialog that starts on Cancel', async ({ page }) => {
  const address = `bounced-${stamp}@example.com`
  await signIn(page, `email-sup-${stamp}@example.com`, 'RTE')
  await openEmail(page)
  await expect(page.getByText('No suppressed addresses').or(page.getByRole('table', { name: 'Suppressed addresses' }))).toBeVisible()
  await page.getByRole('textbox', { name: 'Address' }).fill('not an address')
  await page.getByRole('button', { name: 'Suppress address' }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'Enter one email address' })).toBeVisible()
  await page.getByRole('textbox', { name: 'Address' }).fill(address)
  await page.getByRole('textbox', { name: 'Note (optional)' }).fill('Asked not to be mailed')
  await page.getByRole('button', { name: 'Suppress address' }).click()
  const row = page.getByRole('table', { name: 'Suppressed addresses' }).getByRole('row', { name: new RegExp(address) })
  await expect(row).toContainText('Added by hand')
  await expect(row).toContainText('Asked not to be mailed')
  await page.getByRole('textbox', { name: 'Address' }).fill(address)
  await page.getByRole('button', { name: 'Suppress address' }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'already on the suppression list' })).toBeVisible()

  await page.getByRole('button', { name: `Remove ${address}` }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText(`Remove ${address} from the suppression list?`)
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(row).toBeVisible()
  await page.getByRole('button', { name: `Remove ${address}` }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Remove from list' }).click()
  await expect(page.getByRole('table', { name: 'Suppressed addresses' }).getByRole('row', { name: new RegExp(address) })).toHaveCount(0)
})

test('the bounce-events secret is shown once, with Copy, and dropped when the tab is left; rotating asks first', async ({ page }) => {
  await signIn(page, `email-secret-${stamp}@example.com`, 'ReleaseManager')
  await openEmail(page)
  const make = page.getByRole('button', { name: /^(Make|Rotate) events secret$/ })
  const rotating = (await make.textContent()) === 'Rotate events secret'
  await make.click()
  if (rotating) {
    const dialog = page.getByRole('dialog')
    await expect(dialog).toContainText('Rotate the events secret?')
    await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
    await dialog.getByRole('button', { name: 'Rotate secret' }).click()
  }
  const secret = page.getByTestId('email-events-secret')
  await expect(secret).toBeVisible()
  expect((await secret.textContent())!.length).toBeGreaterThanOrEqual(40)
  await expect(page.getByText('It is shown once and cannot be recovered')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Copy secret' })).toBeVisible()
  await tab(page, 'Developer docs').click()
  await tab(page, 'Email').click()
  await expect(page.getByTestId('email-events-secret')).toHaveCount(0)   // gone: it was never stored where the screen can read it
  await expect(page.getByRole('button', { name: 'Rotate events secret' })).toBeVisible()
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`axe, ${scheme} theme`, () => {
    test.use({ colorScheme: scheme })
    test(`notification settings and the Email tab have no serious or critical violation (${scheme})`, async ({ page }) => {
      await signIn(page, `email-axe-${scheme}-${stamp}@example.com`, 'RTE')
      await openSettings(page)
      await scan(page, `Notification settings (${scheme})`)
      await openEmail(page)
      await scan(page, `Integrations, Email (${scheme})`)
      await page.getByRole('textbox', { name: 'Address' }).fill(`axe-${scheme}-${stamp}@example.com`)
      await page.getByRole('button', { name: 'Suppress address' }).click()
      await page.getByRole('button', { name: new RegExp(`^Remove axe-${scheme}-`) }).click()
      await expect(page.getByRole('dialog')).toBeVisible()
      await scan(page, `Integrations, Email, confirmation dialog (${scheme})`)
      await page.getByRole('dialog').getByRole('button', { name: 'Remove from list' }).click()
    })
  })
}

test('the sidebar has no Notification settings item: it is reached from the Inbox and from Jump to', async ({ page }) => {
  await signIn(page, `email-nav-${stamp}@example.com`, 'RTE')
  await expect(primaryNav(page).getByRole('link', { name: 'Notification settings' })).toHaveCount(0)
})
