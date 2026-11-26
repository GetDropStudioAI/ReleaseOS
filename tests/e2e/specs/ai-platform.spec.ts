import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, signIn } from './support'

// REOS-183 (D58): the AI platform's settings panel on the Modules tab. Read-only facts (module, provider, whether a key is stored: never the key, the models, the global kill switch) and two
// workspace controls that are buttons with confirmation dialogs (pause AI = the workspace kill switch, opt the workspace out), never toggle switches (rule 9). The panel shows under the AI module.
// The shared demo server must end with the AI module off and the workspace controls back to normal: later specs assume it.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

/** Puts AI back to its defaults through the API as the signed-in admin, whatever a failed run left. */
async function reset(page: Page) {
  await page.evaluate(async () => {
    const post = (u: string, h: Record<string, string> = {}) => fetch(u, { method: 'POST', headers: { 'Content-Type': 'application/json', ...h }, body: '{}' })
    await post('/api/v1/ai/workspace:resume'); await post('/api/v1/ai/workspace:opt-in')
    const list = await (await fetch('/api/v1/modules')).json() as { key: string; enabled: boolean; version: number }[]
    for (const m of list.filter(x => x.enabled)) await post(`/api/v1/modules/${m.key}:disable`, { 'If-Match': `"${m.version}"` })
  })
}

test.afterEach(async ({ browser }) => {
  const ctx = await browser.newContext()
  try {
    const page = await ctx.newPage()
    await signIn(page, 'ai-cleanup-rte@example.com', 'RTE')
    await reset(page)
  } finally { await ctx.close() }
})

const panel = (page: Page) => page.getByRole('region', { name: 'AI settings' })

async function openAi(page: Page) {
  await openPage(page, '/admin?tab=modules')
  await expect(page.getByRole('table', { name: 'Modules' })).toBeVisible()
  await page.getByRole('table', { name: 'Modules' }).locator('tbody tr', { hasText: 'AI module' }).click()
  await expect(panel(page)).toBeVisible()
}

async function enableAi(page: Page) {
  await page.getByRole('button', { name: 'Enable AI module' }).click()
  await page.getByRole('dialog', { name: 'Enable AI module?' }).getByRole('button', { name: 'Enable AI module' }).click()
  await expect(page.getByRole('button', { name: 'Disable AI module' })).toBeVisible()
}

test('the panel states the facts in words, with the module off, and never shows or asks for a key', async ({ page }) => {
  await signIn(page, 'ai-facts-rte@example.com', 'RTE')
  await openAi(page)
  const p = panel(page)
  await expect(p).toContainText('AI calls are not made')
  await expect(p).toContainText('module is off')
  await expect(p.locator('dt', { hasText: 'Module' }).locator('xpath=following-sibling::dd[1]')).toHaveText('Not enabled')
  await expect(p.locator('dt', { hasText: 'Provider key' }).locator('xpath=following-sibling::dd[1]')).toHaveText(/^(Not set|Stored|Not needed)/)
  await expect(p.locator('dt', { hasText: 'Small model' })).toBeVisible()
  await expect(p.locator('dt', { hasText: 'Drafting model' })).toBeVisible()
  await expect(p.locator('dt', { hasText: 'Global kill switch' }).locator('xpath=following-sibling::dd[1]')).toHaveText('Off')
  expect(await page.locator('input[type=password]').count()).toBe(0)      // the key is never entered or shown here
  expect(await page.getByRole('switch').count()).toBe(0)                  // rule 9: no toggle switch, no checkbox
  expect(await page.getByRole('checkbox').count()).toBe(0)
})

test('pausing AI for the workspace asks first, can be cancelled, and is undone with another confirmed action', async ({ page }) => {
  await signIn(page, 'ai-pause-rm@example.com', 'ReleaseManager')
  await openAi(page)
  await enableAi(page)
  const p = panel(page)
  await expect(p).toContainText('Running')

  await p.getByRole('button', { name: 'Pause AI for this workspace' }).click()
  const dialog = page.getByRole('dialog', { name: 'Pause AI for this workspace?' })
  await expect(dialog).toContainText('stops every AI call')
  await expect(dialog).toContainText('never approves or changes a status')
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(dialog).toBeHidden()
  await expect(p).toContainText('Running')                                // Cancel changes nothing

  await p.getByRole('button', { name: 'Pause AI for this workspace' }).click()
  await page.getByRole('dialog', { name: 'Pause AI for this workspace?' }).getByRole('button', { name: 'Pause AI for this workspace' }).click()
  await expect(p.locator('dt', { hasText: 'This workspace' }).locator('xpath=following-sibling::dd[1]')).toHaveText('Paused')
  await expect(p).toContainText('AI paused for this workspace')
  await expect(page.getByTestId('announcer')).toContainText('AI paused for this workspace')
  await expect(p).toContainText('AI calls are not made: AI is paused for this workspace')
  expect(await page.evaluate(async () => (await (await fetch('/api/v1/ai/preflight')).json() as { allowed: boolean }).allowed)).toBe(false)

  await p.getByRole('button', { name: 'Resume AI for this workspace' }).click()
  await page.getByRole('dialog', { name: 'Resume AI for this workspace?' }).getByRole('button', { name: 'Resume AI for this workspace' }).click()
  await expect(p.locator('dt', { hasText: 'This workspace' }).locator('xpath=following-sibling::dd[1]')).toHaveText('Running')
  await expect(p.getByRole('button', { name: 'Pause AI for this workspace' })).toBeVisible()
})

test('opting the workspace out is a confirmed action that says no data is sent, and opting back in reverses it', async ({ page }) => {
  await signIn(page, 'ai-optout-rte@example.com', 'RTE')
  await openAi(page)
  const p = panel(page)
  await p.getByRole('button', { name: 'Opt this workspace out of AI' }).click()
  const dialog = page.getByRole('dialog', { name: 'Opt this workspace out of AI?' })
  await expect(dialog).toContainText('no data from this workspace is ever sent to a model')
  await expect(dialog).toContainText('Ticket summaries and customer data are never sent')
  await dialog.getByRole('button', { name: 'Opt this workspace out of AI' }).click()
  await expect(p.locator('dt', { hasText: 'Data use' }).locator('xpath=following-sibling::dd[1]')).toHaveText('Opted out')
  await p.getByRole('button', { name: 'Opt this workspace back in to AI' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Opt this workspace back in to AI' }).click()
  await expect(p.locator('dt', { hasText: 'Data use' }).locator('xpath=following-sibling::dd[1]')).toHaveText('Included')
})

test('with the module on, a call is allowed (or says why not) and the AI endpoints exist; with it off they answer 404', async ({ page }) => {
  await signIn(page, 'ai-guard-rte@example.com', 'RTE')
  expect(await page.evaluate(async () => (await fetch('/api/v1/ai/preflight')).status)).toBe(404)
  await openAi(page)
  await enableAi(page)
  expect(await page.evaluate(async () => (await fetch('/api/v1/ai/preflight')).status)).toBe(200)
  await page.getByRole('button', { name: 'Disable AI module' }).click()
  await page.getByRole('dialog', { name: 'Disable AI module?' }).getByRole('button', { name: 'Disable AI module' }).click()
  await expect(page.getByRole('button', { name: 'Enable AI module' })).toBeVisible()
  expect(await page.evaluate(async () => (await fetch('/api/v1/ai/preflight')).status)).toBe(404)
  await expect(panel(page)).toContainText('module is off')
})

test('only an RTE or Release Manager can read the AI status or change it', async ({ page }) => {
  for (const role of ['Viewer', 'GovernanceOfficer']) {
    await signIn(page, `ai-${role}@example.com`, role)
    const codes = await page.evaluate(async () => [
      (await fetch('/api/v1/ai/status')).status,
      (await fetch('/api/v1/ai/workspace:pause', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).status,
      (await fetch('/api/v1/ai/key', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: '{"key":"x"}' })).status,
    ])
    expect(codes).toEqual([403, 403, 403])
    await page.getByRole('button', { name: /account and appearance/ }).click()
    await page.getByRole('button', { name: 'Sign out', exact: true }).click()
    await expect(page.locator('input[type=email]')).toBeVisible()
  }
})

test('the AI settings panel and its confirmation dialog have no serious axe violation, light or dark', async ({ page }) => {
  await signIn(page, 'ai-axe-rte@example.com', 'RTE')
  await openAi(page)
  await enableAi(page)
  for (const scheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: scheme })
    await openAi(page)
    await scan(page, `AI settings (${scheme})`)
    await panel(page).getByRole('button', { name: 'Pause AI for this workspace' }).click()
    await expect(page.getByRole('dialog', { name: 'Pause AI for this workspace?' })).toBeVisible()
    await scan(page, `AI pause dialog (${scheme})`)
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()
    await expect(page.getByRole('dialog')).toHaveCount(0)
  }
})
