import { expect, type Page } from '@playwright/test'

export async function signIn(page: Page, email: string, role = 'RTE') {
  await page.goto('/')
  await page.fill('input[type=email]', email)
  await page.selectOption('select', role)
  await page.click('button:has-text("Sign in (development)")')
  await expect(page.locator('.stream-row').first()).toBeVisible()
}

/** The shell (REOS-94): the sidebar's destinations, the Inbox bell in the utility bar, and the control-plane card that opens Sync health. */
export const primaryNav = (page: Page) => page.getByRole('navigation', { name: 'Primary' })
export const inboxBell = (page: Page) => page.getByRole('banner').getByRole('link', { name: /^Inbox/ })
export const syncHealthCard = (page: Page) => page.locator('aside[aria-label="Workspace"] a[href="/sync"]')

/** Sign out from the account panel the user card opens; waits for the sign-in page, so the logout request has finished. */
export async function signOut(page: Page) {
  await page.getByRole('button', { name: /account and appearance/ }).click()
  await page.getByRole('button', { name: 'Sign out', exact: true }).click()
  await expect(page.locator('input[type=email]')).toBeVisible()
}

/** Ids of a demo train and its gates, read through the signed-in page's own session. */
export async function trainByTitle(page: Page, title: string) {
  return page.evaluate(async t => {
    const list = await (await fetch('/api/v1/trains')).json() as { id: string; title: string }[]
    const row = list.find(r => r.title.includes(t))!
    const detail = await (await fetch(`/api/v1/trains/${row.id}`)).json() as { id: string; gates: { id: string; name: string; version: number; status: string }[] }
    return detail
  }, title)
}
