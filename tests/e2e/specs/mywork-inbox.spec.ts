import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-38: My work and the notifications inbox. The live drill (Development-only) gives the signed-in RTE a running step (My work)
// and a critical "step started late" notice (Inbox), so no extra seeding endpoint is needed.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function drill(page: Page) {
  return page.evaluate(async () => (await fetch('/api/v1/dev/live-drill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()) as Promise<{ trainId: string; runId: string; steps: Record<string, string> }>
}
async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}
const nav = (page: Page) => page.getByRole('navigation', { name: 'Primary' })
const lateNotice = (page: Page) => page.locator('tbody tr', { hasText: 'Step R-002 started' })

test('My work lists my running step, opens it in its train, and passes axe', async ({ page }) => {
  await signIn(page, 'mywork@example.com')
  const d = await drill(page)
  await page.goto('/work')
  await expect(page.getByRole('heading', { name: 'My work' })).toBeVisible()
  await expect(page.getByTestId('work-counts')).toContainText('assigned to you')
  const row = page.locator('tbody tr', { hasText: 'R-002 Deploy Payments API' })
  await expect(row).toContainText('● running')
  await expect(row).toContainText('Step')
  await expect(page.locator('thead th').filter({ hasText: /^$/ })).toHaveCount(0)   // no empty headers
  await scan(page, 'My work')

  await row.getByRole('button', { name: 'R-002 Deploy Payments API' }).click()
  await expect(page).toHaveURL(new RegExp(`/trains/${d.trainId}/live/steps/${d.steps['R-002']}$`))
})

test('My work keyboard: j moves the row selection, Enter opens the train', async ({ page }) => {
  await signIn(page, 'mywork-keys@example.com')
  const d = await drill(page)
  await page.goto('/work')
  const rows = page.locator('tbody tr')
  await expect(rows.first()).toBeVisible()
  await page.locator('h1').click()          // focus is on the page, not in a control
  await page.keyboard.press('j')
  await expect(rows.first()).toHaveAttribute('aria-selected', 'true')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(new RegExp(`/trains/${d.trainId}`))
})

test('Inbox: unread notice is semibold with words, nav shows the count, opening marks it read', async ({ page }) => {
  await signIn(page, 'inbox@example.com')
  const d = await drill(page)
  await page.goto('/inbox')
  const n = lateNotice(page)
  await expect(n).toBeVisible()
  await expect(n).toContainText('● unread')
  await expect(n).toContainText('level 2')
  await expect(n).toHaveCSS('font-weight', '600')
  await expect(nav(page).getByRole('link', { name: /^Inbox \(\d+\)$/ })).toBeVisible()
  await scan(page, 'Inbox')

  // j selects the newest row (the drill's notice), Enter opens its train's live run and marks it read
  await page.locator('h1').click()
  await page.keyboard.press('j')
  await expect(page.locator('tbody tr').first()).toHaveAttribute('aria-selected', 'true')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(new RegExp(`/trains/${d.trainId}/live`))

  await nav(page).getByRole('link', { name: /^Inbox/ }).click()
  await expect(lateNotice(page)).toContainText('○ read')
  await expect(lateNotice(page)).not.toHaveCSS('font-weight', '600')
  await expect(nav(page).getByRole('link', { name: 'Inbox', exact: true })).toBeVisible()   // nothing unread: no count
})

test('Inbox: Unread only filter, Mark read and Mark all read', async ({ page }) => {
  await signIn(page, 'inbox-filter@example.com')
  await drill(page)
  await drill(page)
  await page.goto('/inbox')
  await expect(page.locator('tbody tr', { hasText: 'Step R-002 started' })).toHaveCount(2)

  const only = page.getByRole('button', { name: 'Unread only' })
  await only.click()
  await expect(only).toHaveAttribute('aria-pressed', 'true')
  await page.getByRole('button', { name: /^Mark read/ }).first().click()
  await expect(page.locator('tbody tr', { hasText: 'Step R-002 started' })).toHaveCount(1)   // read rows leave the unread-only list
  await expect(page.getByTestId('inbox-counts')).toContainText('1 unread')

  await page.getByRole('button', { name: 'Mark all read' }).click()
  await expect(page.getByText('No unread notifications.')).toBeVisible()
  await only.click()
  await expect(page.locator('tbody tr', { hasText: 'Step R-002 started' })).toHaveCount(2)
  await expect(page.locator('tbody tr', { hasText: '● unread' })).toHaveCount(0)
})

test('Inbox: a new notification arrives live and the nav count updates without a reload', async ({ page }) => {
  await signIn(page, 'inbox-live@example.com')
  await page.goto('/inbox')
  await expect(page.getByText('No notifications yet.')).toBeVisible()
  await drill(page)   // the drill's late-start notice is pushed over SignalR (NotificationCreated)
  await expect(page.locator('tbody tr', { hasText: 'Step R-002 started' })).toBeVisible()
  await expect(nav(page).getByRole('link', { name: /^Inbox \(\d+\)$/ })).toBeVisible()
})
