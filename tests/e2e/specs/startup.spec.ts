import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-135 (G13): the pages outside the signed-in shell, in the redesign. Each has the product name, one h1, no serious axe violation in either theme,
// and focus on the h1 (or the first field on the sign-in page). Backend-down, stopped and no-workspace are simulated by intercepting requests.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe violations on ${where}:\n${bad.map(v => `${v.id}: ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')}`).toEqual([])
}

const heading = (page: Page) => page.getByRole('heading', { level: 1 })

test('the document title names the product', async ({ page }) => {
  await page.goto('/')
  await expect(page.locator('input[type=email]')).toBeVisible()
  await expect(page).toHaveTitle('Sign in · ReleaseOS')
  await expect(page.locator('.os-start-brand')).toHaveText('ReleaseOS')
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('sign-in, and the refused sign-in', async ({ page }) => {
      await page.goto('/')
      await expect(heading(page)).toHaveText('Sign in')
      await expect(page.locator('input[type=email]')).toBeFocused()
      await scan(page, `sign-in (${scheme})`)
      await page.goto('/?signin=no-role')
      await expect(page.getByRole('alert').first()).toContainText('Your account has no role in this app')
      await scan(page, `refused sign-in (${scheme})`)
    })

    test('loading', async ({ page }) => {
      await page.route('**/api/v1/me', async route => { await new Promise(r => setTimeout(r, 4000)); await route.continue() })
      await page.goto('/')
      await expect(page.getByRole('status')).toContainText('Loading ReleaseOS')
      await expect(heading(page)).toBeFocused()
      await scan(page, `loading (${scheme})`)
    })

    test('server unreachable', async ({ page }) => {
      await page.route('**/api/v1/me', route => route.abort())
      await page.goto('/')
      await expect(heading(page)).toHaveText('Cannot reach the server')
      await expect(page.getByRole('alert')).toContainText('The server did not answer')
      await expect(heading(page)).toBeFocused()
      await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible()
      await scan(page, `unreachable (${scheme})`)
    })

    test('stopped', async ({ page }) => {
      await signIn(page, `startup-stop-${scheme}@example.com`)
      await page.route('**/control/status', route => route.fulfill({ json: { state: 'ready', message: '', revision: 'x', running: true } }))
      await page.route('**/control/exit', route => route.fulfill({ json: { message: 'Stopping' } }))
      await page.getByRole('button', { name: /account and appearance/ }).click()
      await page.getByRole('button', { name: 'Exit', exact: true }).click()
      await page.getByRole('button', { name: 'Sign out and exit' }).click()
      await expect(heading(page)).toHaveText('ReleaseOS has stopped')
      await expect(page.getByRole('status')).toContainText('You can close this tab.')
      await expect(heading(page)).toBeFocused()
      await scan(page, `stopped (${scheme})`)
    })

    test('no workspace', async ({ page }) => {
      await signIn(page, `startup-none-${scheme}@example.com`)
      await page.route('**/api/v1/workspaces', route => route.fulfill({ json: [] }))
      await page.reload()
      await expect(heading(page)).toHaveText('No workspace')
      await expect(heading(page)).toBeFocused()
      await scan(page, `no workspace (${scheme})`)
      await page.getByRole('button', { name: 'Sign out' }).click()
      await expect(page.locator('input[type=email]')).toBeVisible()
    })

    test('unknown path', async ({ page }) => {
      await signIn(page, `startup-404-${scheme}@example.com`)
      await page.goto('/no-such-page')
      await expect(heading(page)).toHaveText('Page not found')
      await expect(heading(page)).toBeFocused()
      await expect(page).toHaveTitle('Page not found · ReleaseOS')
      await scan(page, `unknown path (${scheme})`)
      await page.getByRole('link', { name: 'Go to Releases' }).click()
      await expect(page.getByRole('table', { name: 'Releases' })).toBeVisible()
    })
  })
}
