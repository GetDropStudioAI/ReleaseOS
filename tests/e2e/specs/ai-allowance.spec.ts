import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, signIn } from './support'

// REOS-185 (D57, D58): the AI allowance and usage panel under the AI module on the Modules tab. An administrator sees what the month includes, what is used and left, the caps in words, and the usage
// by feature, person and ledger entry; the state is a word and a chip as well as the meter (never colour alone); nothing on it adds credits and there are no checkboxes or toggle switches (rule 9).
// The e2e server has no provider key, so no call is made and the month is unused: the numbers are the included allowance. The calls, the caps and the refusals are proved by the API and unit tests
// with the fake provider and the frozen clock. The shared demo server must end with the AI module off: later specs assume it.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

/** Puts the modules back to off through the API as the signed-in admin, whatever a failed run left. */
async function reset(page: Page) {
  await page.evaluate(async () => {
    const post = (u: string, h: Record<string, string> = {}) => fetch(u, { method: 'POST', headers: { 'Content-Type': 'application/json', ...h }, body: '{}' })
    const list = await (await fetch('/api/v1/modules')).json() as { key: string; enabled: boolean; version: number }[]
    for (const m of list.filter(x => x.enabled)) await post(`/api/v1/modules/${m.key}:disable`, { 'If-Match': `"${m.version}"` })
  })
}

test.afterEach(async ({ browser }) => {
  const ctx = await browser.newContext()
  try {
    const page = await ctx.newPage()
    await signIn(page, 'ai-allowance-cleanup-rte@example.com', 'RTE')
    await reset(page)
  } finally { await ctx.close() }
})

const panel = (page: Page) => page.getByRole('region', { name: 'AI allowance and usage' })

async function openAi(page: Page) {
  await openPage(page, '/admin?tab=modules')
  await expect(page.getByRole('table', { name: 'Modules' })).toBeVisible()
  await page.getByRole('table', { name: 'Modules' }).locator('tbody tr', { hasText: 'AI module' }).click()
  await expect(panel(page)).toBeVisible()
}

test('the allowance is shown in words while the module is off: the month, what is included, used and left, and when it renews', async ({ page }) => {
  await signIn(page, 'ai-allowance-rte@example.com', 'RTE')
  await openAi(page)
  const p = panel(page)
  await expect(p).toContainText('(UTC month). Renews on')
  await expect(p).toContainText(/Renews on 1 \w+ \d{4} \(UTC\)/)
  await expect(p.getByText('Within allowance')).toBeVisible()                       // the state is a word in a chip, not only a colour
  const metrics = p.getByRole('group', { name: 'AI allowance this month' })
  await expect(metrics.getByRole('group', { name: 'Included' })).toContainText('300')
  await expect(metrics.getByRole('group', { name: 'Included' })).toContainText('credits')
  await expect(metrics.getByRole('group', { name: 'Used' })).toContainText('0')
  await expect(metrics.getByRole('group', { name: 'Remaining' })).toContainText('300')
  await expect(metrics.getByRole('group', { name: 'Chat' })).toContainText('50%')
  await expect(p.getByRole('progressbar', { name: /AI allowance used: 0 percent/ })).toBeVisible()
  await expect(p).toContainText('0% used of 300 credits')
  await expect(p).toContainText('Chat is capped first: it stops at 50% of the allowance, or at the 80% soft cap')
  await expect(p).toContainText('Drafts and summaries go on until the allowance is used up (100%)')
})

test('usage by feature lists every feature, chat marked as capped first, and the empty tables say what will appear', async ({ page }) => {
  await signIn(page, 'ai-allowance-rte2@example.com', 'RTE')
  await openAi(page)
  const p = panel(page)
  const features = p.getByRole('table', { name: 'Usage by feature this month' })
  await expect(features).toBeVisible()
  await expect(features.locator('tbody tr', { hasText: 'Question about release data' })).toContainText('Chat: capped first')
  await expect(features.locator('tbody tr', { hasText: 'Release notes draft' })).toContainText('Drafts and summaries')
  await expect(features.locator('tbody tr')).toHaveCount(7)
  await expect(p.getByText('No one has used AI this month')).toBeVisible()
  await expect(p.getByText('No AI call has asked for allowance yet')).toBeVisible()
  await expect(p).toContainText('Credit packs add to the month they are bought in and are not editable here')
})

test('nothing on the panel adds credits or toggles anything, and the page passes the accessibility scan', async ({ page }) => {
  await signIn(page, 'ai-allowance-rte3@example.com', 'RTE')
  await openAi(page)
  const p = panel(page)
  await expect(p.getByRole('button', { name: 'Refresh usage' })).toBeVisible()
  await expect(p.getByRole('button', { name: /credit|add|grant|buy/i })).toHaveCount(0)
  await expect(p.locator('input[type=checkbox], [role=switch], [role=checkbox]')).toHaveCount(0)
  await p.getByRole('button', { name: 'Refresh usage' }).click()
  await expect(p).toContainText('Within allowance')
  await scan(page, 'the AI allowance panel')
})

test('the allowance answers over the API for administrators and there is no way to add credits', async ({ page }) => {
  await signIn(page, 'ai-allowance-rte4@example.com', 'RTE')
  const u = await page.evaluate(async () => { const r = await fetch('/api/v1/ai/usage'); return { status: r.status, body: await r.json() } })
  expect(u.status).toBe(200)
  expect(u.body.totalMicroUsd).toBe(3_000_000)
  expect(u.body.level).toBe('Ok')
  const add = await page.evaluate(async () => (await fetch('/api/v1/ai/usage/packs', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"credits":1000}' })).status)
  expect([404, 405]).toContain(add)                                                   // there is no way for an administrator to give their own workspace credits
})
