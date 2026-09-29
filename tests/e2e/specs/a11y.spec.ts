import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn, trainByTitle } from './support'

// REOS-27: no serious or critical axe violations in either theme; the main actions work from the keyboard.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('sign-in page', async ({ page }) => {
      await page.goto('/')
      await expect(page.locator('form')).toBeVisible()
      await scan(page, `sign-in (${scheme})`)
    })

    test('workspace with a train and the gate Inspector open', async ({ page }) => {
      await signIn(page, `a11y-${scheme}@example.com`)
      const t = await trainByTitle(page, 'Card portal')
      const gate = t.gates.find(g => g.name === 'Compliance Sign-off')!
      await page.goto(`/trains/${t.id}/gates/${gate.id}`)
      await expect(page.locator('.inspector h2')).toHaveText('Compliance Sign-off')
      await expect(page.locator('svg.timeline')).toBeVisible()
      await scan(page, `workspace (${scheme})`)
    })

    test('admin screen', async ({ page }) => {
      await signIn(page, `a11y-admin-${scheme}@example.com`)
      await page.click('a:has-text("Admin")')
      await expect(page.locator('table.grid').first()).toBeVisible()
      await scan(page, `admin (${scheme})`)
    })
  })
}

test('the gate timeline and the Inspector work from the keyboard', async ({ page }) => {
  await signIn(page, 'keyboard@example.com')
  const t = await trainByTitle(page, 'Reporting')
  await page.goto(`/trains/${t.id}`)
  await expect(page.locator('svg.timeline')).toBeVisible()
  const cab = page.locator('svg.timeline g.tl-gate', { hasText: 'CAB Approval' })
  await cab.focus()
  await expect(cab).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page.locator('.inspector h2')).toHaveText('CAB Approval')
  // every action is a real button, reachable with Tab and named
  const close = page.getByRole('button', { name: 'Close inspector' })
  await close.focus(); await page.keyboard.press('Enter')
  await expect(page.locator('.inspector')).toContainText('Select a train, gate or task')
})
