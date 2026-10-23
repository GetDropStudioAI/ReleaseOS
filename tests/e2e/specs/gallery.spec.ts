import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import { signIn } from './support'

// REOS-93: the redesign's components in the gallery (/gallery), light and dark side by side. No serious axe violation in either half, a visible
// focus ring on every interactive component, status chips that say their status in words, tabs that work from the keyboard, and a confirmation
// dialog that starts on Cancel, closes on Escape and gives focus back.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

test.beforeEach(async ({ page }) => {
  await signIn(page, 'gallery@example.com')
  await page.goto('/gallery')
  await expect(page.locator('section[aria-label="Light theme"] > h2')).toHaveText('Light')
})

test('no serious axe violation in either theme of the gallery', async ({ page }) => {
  for (const half of ['Light theme', 'Dark theme']) {
    const r = await new AxeBuilder({ page }).include(`section[aria-label="${half}"]`).withTags(TAGS).analyze()
    const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
    const detail = bad.map(v => `${v.id}: ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
    expect(bad, `${half}:\n${detail}`).toEqual([])
  }
})

test('every status chip names its status in words; the dark half really is dark', async ({ page }) => {
  const chips = page.locator('section[aria-label="Light theme"] .os-chip')
  expect(await chips.count()).toBeGreaterThanOrEqual(15)
  for (const text of await chips.allInnerTexts()) expect(text.trim().length).toBeGreaterThan(2)
  const bg = (half: string) => page.locator(`section[aria-label="${half}"]`).evaluate(el => getComputedStyle(el).backgroundColor)
  expect(await bg('Light theme')).not.toEqual(await bg('Dark theme'))
})

test('focus rings are visible on buttons, tabs, table rows, the filter and the search field', async ({ page }) => {
  const light = page.locator('section[aria-label="Light theme"]')
  const ring = async (focusTarget: string, ringTarget = focusTarget) => {
    await light.locator(focusTarget).first().focus()
    await page.keyboard.press('Shift+Tab'); await page.keyboard.press('Tab')   // keyboard focus, so :focus-visible applies
    const outline = await light.locator(ringTarget).first().evaluate(el => getComputedStyle(el).outlineStyle)
    expect(outline, ringTarget).not.toBe('none')
  }
  await ring('.os-button.primary')
  await ring('.os-tab[aria-selected="true"]')
  await ring('.os-table.selectable tbody tr')
  await ring('.os-filter select', '.os-filter')
  await ring('.os-search input', '.os-search')
})

test('tabs move with the arrow keys, and Home and End', async ({ page }) => {
  const light = page.locator('section[aria-label="Light theme"]')
  await light.locator('.os-tab[aria-selected="true"]').focus()
  await page.keyboard.press('ArrowRight')
  await expect(light.locator('.os-tab[aria-selected="true"]')).toHaveText('Services 6')
  await expect(light.locator('[role=tabpanel]')).toContainText('Services content')
  await page.keyboard.press('End')
  await expect(light.locator('.os-tab[aria-selected="true"]')).toHaveText('Activity')
  await page.keyboard.press('ArrowRight')   // wraps
  await expect(light.locator('.os-tab[aria-selected="true"]')).toHaveText('Overview & readiness')
})

test('table rows are selected with the keyboard', async ({ page }) => {
  const rows = page.locator('section[aria-label="Light theme"] .os-table tbody tr')
  await rows.nth(0).focus()
  await page.keyboard.press('ArrowDown')
  await page.keyboard.press('Enter')
  await expect(rows.nth(1)).toHaveAttribute('aria-current', 'true')
  await expect(rows.nth(0)).not.toHaveAttribute('aria-current', 'true')
})

test('the confirmation dialog starts on Cancel, closes on Escape and returns focus', async ({ page }) => {
  const light = page.locator('section[aria-label="Light theme"]')
  const trigger = light.getByRole('button', { name: 'Rollback' })
  await trigger.click()
  const dialog = page.getByRole('dialog', { name: 'Roll back checkout-api in Production EU?' })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
  await expect(trigger).toBeFocused()
})
