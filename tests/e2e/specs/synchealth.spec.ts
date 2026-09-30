import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-42: Sync health (screen 5), the connector-wide banner on every screen, the live SyncAlertRaised push, and the webhook allowlist.
// Alerts are seeded through the Development-only /api/v1/dev/sync/* endpoints (SyncDevEndpoints); `raise` goes through the real SyncAlertWriter,
// so the push a client sees is the push production sends. The dev reset at the start and in `finally` leaves a clean screen for the other specs.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}

const dev = (page: Page, path: string, body: unknown = {}) =>
  page.evaluate(async ([p, b]) => {
    const r = await fetch(`/api/v1/dev/sync/${p as string}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(b) })
    return { status: r.status, body: await r.json().catch(() => null) }
  }, [path, body] as const)

const syncLink = (page: Page) => page.locator('nav[aria-label=Primary] a[href="/sync"]')
const openRow = (page: Page, text: string) => page.locator('table[aria-label="Open alerts"] tbody tr', { hasText: text })

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('an alert appears live, the banner shows on another screen, the detail works from the keyboard and resolving keeps the history', async ({ page, browser }) => {
      const tag = `${scheme}-${Date.now()}`
      const authMsg = `401 on GET /api/now/table/change_request [${tag}]`
      const backupMsg = `Backup folder is not writable [${tag}]`

      await signIn(page, `sync-${scheme}@example.com`, 'RTE')
      expect((await dev(page, 'reset')).status).toBe(200)
      expect((await dev(page, 'connector', { source: 'Jira', consecutiveFailures: 0 })).status).toBe(200)
      expect((await dev(page, 'connector', { source: 'ServiceNow', consecutiveFailures: 0 })).status).toBe(200)

      // a second person, on a different screen, in a second browser
      const other = await browser.newContext({ colorScheme: scheme })
      const b = await other.newPage()
      try {
        await signIn(b, `sync-rm-${scheme}@example.com`, 'ReleaseManager')
        await b.goto('/inbox')
        await expect(b.locator('h1')).toBeVisible()
        await expect(b.getByTestId('sync-banner')).toHaveText('')            // nothing failing: the banner row is empty

        await syncLink(page).click()
        await expect(page).toHaveURL(/\/sync$/)
        await expect(page.locator('h1')).toHaveText('Sync health')
        await expect(page.locator('table[aria-label="Connectors"] tbody tr')).toHaveCount(2)
        await expect(page.locator('input[type=checkbox], [role=switch], [role=dialog]')).toHaveCount(0)   // rule 9
        await expect(page.getByTestId('sync-banner')).toHaveText('')

        // LIVE: the alert is raised on the server; neither page reloads
        const raised = await dev(page, 'raise', { source: 'ServiceNow', kind: 'AuthFailed', key: tag, message: authMsg })
        expect(raised.status).toBe(200)
        await expect(openRow(page, authMsg)).toBeVisible()
        await expect(openRow(page, authMsg)).toContainText('AuthFailed')
        await expect(openRow(page, authMsg).locator('td.n')).toHaveText('1')

        // BANNER: the other person, on the inbox screen, sees the connector-wide failure without doing anything, with a text link to Sync health
        const banner = b.getByTestId('sync-banner')
        await expect(banner).toContainText('ServiceNow rejected our credentials')
        await expect(banner).toContainText('not current')
        await expect(page.getByTestId('sync-banner')).toContainText('ServiceNow rejected our credentials')       // and on the Sync health screen itself
        await expect(b.locator('.stream-row').first()).toBeVisible()                                              // the regions below the banner are still laid out
        await b.goto('/')
        await expect(banner).toContainText('ServiceNow rejected our credentials')                                 // another screen again, after a reload
        // the banner is words and a glyph on a tinted line: no box, no pill
        await expect(banner.locator('p').first()).toContainText('✗')
        expect(await banner.locator('p').first().evaluate(el => getComputedStyle(el).borderRadius)).toBe('0px')
        await banner.locator('a:text-is("Sync health")').click()
        await expect(b).toHaveURL(/\/sync$/)

        // repeats are counted on the one row (never a row per failure)
        await dev(page, 'raise', { source: 'ServiceNow', kind: 'AuthFailed', key: tag, message: authMsg, repeat: 3 })
        await expect(openRow(page, authMsg).locator('td.n')).toHaveText('4')
        await expect(openRow(page, authMsg)).toHaveCount(1)

        // a non-connector failure is an alert on the screen, not a banner line of its own
        await dev(page, 'raise', { source: 'Backup', kind: 'BackupFailed', key: tag, message: backupMsg })
        await expect(openRow(page, backupMsg)).toBeVisible()
        await expect(page.getByTestId('sync-banner').locator('p')).toHaveCount(1)

        // DETAIL + KEYBOARD: Details opens the right-hand column; j / k move the selection, Enter focuses the detail, Esc closes it
        const detail = page.locator('aside[aria-label="Alert detail"]')
        await openRow(page, authMsg).locator('button:text-is("Details")').click()
        await expect(openRow(page, authMsg)).toHaveAttribute('aria-selected', 'true')
        await expect(detail.locator('h2')).toHaveText('ServiceNow AuthFailed')
        await expect(detail).toContainText('Likely cause')
        await expect(detail).toContainText('To clear it')
        await expect(detail).toContainText(authMsg)
        await page.keyboard.press('j')
        await expect(page.locator('table.sync-alerts tr[aria-selected="true"]')).toHaveCount(1)
        await page.keyboard.press('k')
        await expect(page.locator('table.sync-alerts tr[aria-selected="true"]')).toHaveCount(1)
        await openRow(page, authMsg).locator('button:text-is("Details")').click()
        await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur())     // Enter on a focused button would just press it
        await page.keyboard.press('Enter')
        await expect(detail.locator('button:text-is("Close (Esc)")')).toBeFocused()
        await scan(page, `sync health with an alert open (${scheme})`)                        // banner, tables, detail, allowlist form: both themes
        await page.keyboard.press('Escape')
        await expect(page.locator('table.sync-alerts tr[aria-selected="true"]')).toHaveCount(0)
        await expect(detail).toContainText('Select an alert')

        // RESOLVE: inline confirm (no modal); the row leaves Open, stays in the history, and the banner clears everywhere
        await openRow(page, authMsg).locator('button:text-is("Details")').click()
        await detail.locator('button:text-is("Resolve now")').click()
        await expect(detail.locator('[role=group][aria-label="Confirm resolve"]')).toContainText('The row stays in the history')
        await detail.locator('button:text-is("Confirm")').click()
        await expect(openRow(page, authMsg)).toHaveCount(0)
        const history = page.locator('table[aria-label="Resolved alerts"] tbody tr', { hasText: authMsg })
        await expect(history).toHaveCount(1)
        await expect(history).toContainText('4')
        await expect(history).toContainText(`sync-${scheme}`)                                // who resolved it (the dev sign-in name is the email's local part)
        await expect(page.getByTestId('sync-banner')).toHaveText('')
        await expect(b.getByTestId('sync-banner')).toHaveText('')                                // the other person's banner cleared by the push
        await scan(page, `sync health with resolved history (${scheme})`)
      } finally {
        await other.close()
        await dev(page, 'reset')
      }
    })

    test('the webhook allowlist: HTTPS only, no credentials, the server refuses private targets, the secret path is never shown, remove confirms inline', async ({ page }) => {
      const secret = `s3cr3t-${scheme}-${Date.now()}`
      await signIn(page, `sync-hooks-${scheme}@example.com`, 'RTE')
      await syncLink(page).click()
      const section = page.locator('section[aria-label="Webhook allowlist"]')
      await expect(section.locator('h2')).toContainText('Webhook allowlist')
      const form = section.locator('form[aria-label="Add a webhook"]')
      const add = form.locator('button:text-is("Add")')
      const hint = section.locator('#wh-hint')
      const name = `#ops-${scheme}-${Date.now()}`

      await form.locator('label:has-text("Name") input').fill(name)
      await form.locator('label:has-text("Address") input').fill('http://hooks.slack.com/services/T0/B0/x')
      await expect(hint).toContainText('Only https:// addresses are allowed')
      await expect(add).toBeDisabled()
      await form.locator('label:has-text("Address") input').fill('https://user:pw@hooks.slack.com/services/T0/B0/x')
      await expect(hint).toContainText('Remove the user name and password')
      await expect(add).toBeDisabled()

      // the browser cannot know a host is private, so the server says so
      await form.locator('label:has-text("Address") input').fill('https://127.0.0.1/hook')
      await expect(add).toBeEnabled()
      await add.click()
      await expect(section.locator('[role=alert]')).toContainText('loopback, private, link-local or internal')

      await form.locator('label:has-text("Address") input').fill(`https://hooks.slack.com/services/T0/B0/${secret}`)
      await expect(hint).not.toContainText('✗')
      await add.click()
      const row = section.locator('table[aria-label="Webhook allowlist"] tbody tr', { hasText: name })
      await expect(row).toBeVisible()
      await expect(row).toContainText('Slack')                                        // detected from the host
      await expect(row).toContainText('https://hooks.slack.com/…')
      expect(await page.content()).not.toContain(secret)                              // the token path is never sent back to the page

      // the same address again is refused as a duplicate
      await form.locator('label:has-text("Name") input').fill(`${name} again`)
      await form.locator('label:has-text("Address") input').fill(`HTTPS://Hooks.Slack.com:443/services/T0/B0/${secret}`)
      await add.click()
      await expect(section.locator('[role=alert]')).toContainText('already on the allowlist')

      await scan(page, `webhook allowlist (${scheme})`)

      // remove: inline confirm, no modal
      await row.locator('button:text-is("Remove")').click()
      await expect(row).toContainText(`Remove ${name}?`)
      await expect(page.locator('[role=dialog]')).toHaveCount(0)
      await row.locator('button:text-is("Confirm")').click()
      await expect(row).toHaveCount(0)
    })

    test('a Viewer reads the screen but is offered no write actions', async ({ page }) => {
      await signIn(page, `sync-viewer-${scheme}@example.com`, 'Viewer')
      await syncLink(page).click()
      await expect(page.locator('h1')).toHaveText('Sync health')
      await expect(page.locator('section[aria-label="Webhook allowlist"]')).toContainText('Only RTEs and Release Managers change the allowlist.')
      await expect(page.locator('form[aria-label="Add a webhook"]')).toHaveCount(0)
      await expect(page.locator('a[href="/connectors"]')).toHaveCount(0)              // the Connectors admin screen is not offered either
    })
  })
}
