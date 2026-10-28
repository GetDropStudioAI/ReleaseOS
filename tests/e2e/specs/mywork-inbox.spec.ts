import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn, inboxBell } from './support'

// REOS-38: My work and the notifications inbox. The live drill (Development-only) gives the signed-in RTE a running step (My work)
// and a critical "step started late" notice (Inbox), so no extra seeding endpoint is needed.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

/** Each drill makes its own train with a unique title ("R26.9AB12 Live drill"); `title` is how a test tells its own notices from every other spec's. */
async function drill(page: Page) {
  return page.evaluate(async () => {
    const d = await (await fetch('/api/v1/dev/live-drill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()
    const t = await (await fetch(`/api/v1/trains/${d.trainId}`)).json()
    return { ...d, title: t.title }
  }) as Promise<{ trainId: string; runId: string; steps: Record<string, string>; title: string }>
}
async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}
// The drill notifies every RTE, so notices from other specs (running in parallel workers) land in the same inbox at any moment. A test therefore counts and
// clicks only the notices of the trains it drilled, found by the drill train's unique title, and never "the first row of that kind" or "no unread at all".
const escape = (t: string) => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
const ownNotices = (page: Page, ...titles: string[]) =>
  page.locator('tbody tr', { hasText: 'Step R-002 started' }).filter({ hasText: new RegExp(titles.map(escape).join('|')) })

test('My work lists my running step, opens it in its train, and passes axe', async ({ page }) => {
  await signIn(page, 'mywork@example.com')
  const d = await drill(page)
  await page.goto('/work')
  await expect(page.getByRole('heading', { name: 'My work' })).toBeVisible()
  await expect(page.getByTestId('work-counts')).toContainText('assigned to you')
  const row = page.locator('tbody tr', { hasText: 'R-002 Deploy Payments API' })
  await expect(row).toContainText('Running')
  await expect(row).toContainText('Step')
  await expect(page.locator('thead th').filter({ hasText: /^$/ })).toHaveCount(0)   // no empty headers
  await scan(page, 'My work')

  await row.getByRole('button', { name: 'R-002 Deploy Payments API' }).click()
  await expect(page).toHaveURL(new RegExp(`/orchestration/${d.trainId}/live/steps/${d.steps['R-002']}$`))
})

test('My work keyboard: j moves the row selection, Enter opens the train', async ({ page }) => {
  await signIn(page, 'mywork-keys@example.com')
  const d = await drill(page)
  await page.goto('/work')
  const rows = page.locator('tbody tr')
  await expect(rows.first()).toBeVisible()
  await page.locator('h1').click()          // focus is on the page, not in the table: j is not a global shortcut (WCAG 2.1.4)
  await page.keyboard.press('j')
  await expect(page.locator('tbody [aria-current="true"]')).toHaveCount(0)
  const first = rows.first().locator('[data-rownav]')
  await first.focus()                        // in the table: k on the first row keeps it, selects it and keeps focus on its button
  await page.keyboard.press('k')
  await expect(first).toHaveAttribute('aria-current', 'true')
  await expect(first).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(new RegExp(`/orchestration/${d.trainId}`))   // a step opens in Orchestration (REOS-128)
})

test('Inbox: unread notice is semibold with words, nav shows the count, opening marks it read', async ({ page }) => {
  await signIn(page, 'inbox@example.com')
  const d = await drill(page)
  await page.goto('/inbox')
  const lateNotice = () => ownNotices(page, d.title)
  const n = lateNotice()
  await expect(n).toBeVisible()
  await expect(n.locator('.os-chip', { hasText: /^Unread$/ })).toBeVisible()
  await expect(n.locator('.os-chip', { hasText: /^Step late$/ })).toBeVisible()   // the kind chip
  await expect(n).toContainText('level 1')                                         // the escalation chip: "Escalation level 1"
  await expect(n).toHaveCSS('font-weight', '600')
  await expect(inboxBell(page)).toHaveAccessibleName(/^Inbox, \d+ unread$/)
  await scan(page, 'Inbox')

  // In the table, j / k move the selection and Enter opens the selected notice's train (live run) and marks it read. The drill's notice is not always
  // the newest row: in a full run an earlier spec's failing connector keeps notifying every RTE (sync alerts, which have no train to open), so walk to it.
  const rows = page.locator('tbody tr')
  const target = await rows.evaluateAll((trs, title) => trs.findIndex(tr => tr.textContent?.includes('Step R-002 started') && tr.textContent.includes(title)), d.title)
  expect(target).toBeGreaterThanOrEqual(0)
  const drillRow = rows.nth(target).locator('[data-rownav]')
  await drillRow.focus()
  await page.keyboard.press('k')
  if (target > 0) await page.keyboard.press('j')   // k moved up one; j comes back, so the selection arrived by keyboard either way
  await expect(drillRow).toHaveAttribute('aria-current', 'true')
  await expect(drillRow).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(new RegExp(`/orchestration/${d.trainId}/live`))

  await inboxBell(page).click()
  await expect(lateNotice().locator('.os-chip', { hasText: /^Read$/ })).toBeVisible()
  await expect(lateNotice()).not.toHaveCSS('font-weight', '600')
})

test('Inbox: Unread only filter, Mark read and Mark all read', async ({ page }) => {
  await signIn(page, 'inbox-filter@example.com')
  const [d1, d2] = [await drill(page), await drill(page)]
  await page.goto('/inbox')
  const late = ownNotices(page, d1.title, d2.title)      // this test's two notices, whatever other specs add to the inbox meanwhile
  await expect(late).toHaveCount(2)
  const total = 2

  const only = page.getByRole('button', { name: 'Unread only' })
  await only.click()
  await expect(only).toHaveAttribute('aria-pressed', 'true')
  await expect(late).toHaveCount(total)
  await late.first().getByRole('button', { name: /^Mark read/ }).click()   // this row's own button, not the first unread row of any kind
  await expect(late).toHaveCount(total - 1)              // a read row leaves the unread-only list

  await page.getByRole('button', { name: 'Mark all read' }).click()
  await expect(late).toHaveCount(0)                      // none of this test's notices is unread (other specs' new notices may be, so the whole list is not asserted empty)
  await only.click()
  await expect(late).toHaveCount(total)
  await expect(late.locator('.os-chip', { hasText: /^Unread$/ })).toHaveCount(0)
  await expect(late.locator('.os-chip', { hasText: /^Read$/ })).toHaveCount(total)
})

test('Inbox: a new notification arrives live and the nav count updates without a reload', async ({ page }) => {
  await signIn(page, 'inbox-live@example.com')
  await page.goto('/inbox')
  await expect(page.getByRole('button', { name: 'Unread only' })).toBeVisible()   // the page is loaded; not "empty", because other specs' drills notify every RTE meanwhile
  const d = await drill(page)   // the drill's late-start notice is pushed over SignalR (NotificationCreated)
  await expect(ownNotices(page, d.title)).toBeVisible()
  await expect(inboxBell(page)).toHaveAccessibleName(/^Inbox, \d+ unread$/)
})
