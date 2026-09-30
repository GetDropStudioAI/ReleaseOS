import { expect, test } from '@playwright/test'
import { signIn } from './support'

// REOS-22: the Stream matches the mockup: groups, blocker counts, gate glyphs, and the freeze footer.
test('the Stream shows groups, blockers, gate glyphs and the freeze footer', async ({ page }) => {
  await signIn(page, 'stream@example.com')
  const stream = page.locator('aside[aria-label=Stream]')
  await expect(stream.locator('h2', { hasText: 'Gated' })).toBeVisible()
  await expect(stream.locator('h2', { hasText: 'Planning' })).toBeVisible()
  await expect(stream.locator('.stream-row', { hasText: 'Card portal' })).toContainText('▲ 2 blockers')
  await expect(stream.locator('.stream-row', { hasText: 'Card portal' }).locator('.stream-gates')).toBeVisible()

  const footer = stream.locator('[aria-label="Freeze ahead"], .freeze-footer')
  await expect(footer).toContainText('Freeze ahead')
  await expect(footer).toContainText('Q4 close')
  await expect(footer).toContainText('Scope: Payments*')
  await expect(footer).toContainText('1 override granted')
})
