import { expect, type Page } from '@playwright/test'

export async function signIn(page: Page, email: string, role = 'RTE') {
  await page.goto('/')
  await page.fill('input[type=email]', email)
  await page.selectOption('select', role)
  await page.click('button:has-text("Sign in (development)")')
  await expect(page.locator('.stream-row').first()).toBeVisible()
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
