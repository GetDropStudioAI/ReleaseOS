import { expect, test } from '@playwright/test'
import { signIn } from './support'

// REOS-152: Safari (macOS WebKit) does not focus a button when it is clicked with the mouse (a click is not a focus event there), so a dialog that
// remembers `document.activeElement` records <body> and cannot give focus back. Emulated in every engine by cancelling the default action of
// mousedown on buttons, which is what stops the focus; the macOS WebKit job of the cross-browser workflow is the real evidence.
test('a confirmation dialog opened with a click that does not focus the button gives focus back on Escape', async ({ page }) => {
  await page.addInitScript(() => document.addEventListener('mousedown', e => { if ((e.target as Element).closest('button')) e.preventDefault() }, true))
  await signIn(page, 'gallery@example.com')
  await page.goto('/gallery')
  const trigger = page.locator('section[aria-label="Light theme"]').getByRole('button', { name: 'Rollback' })
  await trigger.click()
  await expect(trigger).not.toBeFocused()   // the emulation works: the click left focus on <body>
  const dialog = page.getByRole('dialog', { name: 'Roll back checkout-api in Production EU?' })
  await expect(dialog).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
  await expect(trigger).toBeFocused()
})
