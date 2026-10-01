import { expect, test } from '@playwright/test'

// REOS-65: a refused organisation sign-in comes back to the sign-in page as /?signin=<reason>. The page says why in words (no modal, no exception
// text), and drops the code from the address so a reload is clean. The server side is covered by SessionEndAndSignInTests (no IdP runs in e2e).
test('a refused organisation sign-in is explained on the sign-in page', async ({ page }) => {
  await page.goto('/?signin=no-role')
  const alert = page.getByRole('alert')
  await expect(alert).toContainText('Your account has no role in this app')
  await expect(page.locator('button:has-text("Sign in (development)")')).toBeVisible()   // the page is still the sign-in page, usable at once
  await expect(page).not.toHaveURL(/signin=/)

  await page.reload()
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('a code the page does not know shows the generic message, never the text it carries', async ({ page }) => {
  await page.goto('/?signin=' + encodeURIComponent('Call 555-0100 to restore access'))
  await expect(page.getByRole('alert')).toContainText('Organisation sign-in did not complete')
  await expect(page.getByText('555-0100')).toHaveCount(0)
})
