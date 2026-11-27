import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { openPage, primaryNav, signIn } from './support'

// REOS-165: the Integrations page (/integrations, Management group, RTE and Release Manager only): an overview with the health of tokens and webhook deliveries, the API tokens and Webhooks
// screens as tabs, and the developer documentation. The first test is the scripted walkthrough of docs/DEVELOPERS.md: a new developer makes a token, calls the API with it, adds a webhook, sends a test
// event and sees it in the delivery log, using only what the guide and the page say. The rest: states in words, no checkbox or switch, the tab keyboard, a role without access, axe in both themes.
// Adds its own token and webhooks (unique names) to the shared demo database and removes them again.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now() % 1_000_000
const GUIDE = readFileSync(new URL('../../../docs/DEVELOPERS.md', import.meta.url), 'utf8')

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

const tab = (page: Page, name: string | RegExp) => page.getByRole('tablist', { name: 'Integrations sections' }).getByRole('tab', { name })
async function openIntegrations(page: Page) {
  await openPage(page, '/integrations')
  await expect(page.getByRole('heading', { level: 1, name: 'Integrations' })).toBeVisible()
  await expect(page.getByRole('group', { name: 'Delivery health' })).toBeVisible()   // the overview has loaded
}
/** Removes a webhook the test made, whatever version it is at now. */
async function dropWebhook(page: Page, id: string) {
  await page.evaluate(async i => {
    const o = await (await fetch('/api/v1/webhooks')).json() as { subscriptions: { id: string; version: number }[] }
    const v = o.subscriptions.find(s => s.id === i)?.version
    if (v !== undefined) await fetch(`/api/v1/webhooks/${i}`, { method: 'DELETE', headers: { 'If-Match': String(v) } })
  }, id)
}

test('the scripted walkthrough of the developer guide: make a token, call the API with it, add a webhook, send a test event, see it delivered or retried, revoke the token', async ({ page, request }) => {
  const tokenName = `E2E walkthrough ${stamp}`, hookName = `E2E walkthrough hook ${stamp}`
  // the guide and the page say the same thing about the first call
  expect(GUIDE).toContain('Authorization: Bearer $RELEASEOS_TOKEN')
  expect(GUIDE).toContain('/api/public/v1/me')

  await signIn(page, 'int-walk-rte@example.com', 'RTE')
  await primaryNav(page).getByRole('link', { name: 'Integrations' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Integrations' })).toBeVisible()

  // Quickstart 1: make a token with Read access, copy the secret shown once
  await tab(page, 'API tokens').click()
  await page.getByRole('button', { name: 'Make token' }).first().click()
  const f = page.getByRole('form', { name: 'Make token' })
  await f.getByLabel('Name').fill(tokenName)
  await expect(f.getByLabel('Access')).toHaveValue('read')   // the guide says to leave Access on Read
  await f.getByRole('button', { name: 'Make token' }).click()
  const secret = (await page.getByTestId('token-secret').innerText()).trim()
  expect(secret).toMatch(/^rt_[A-Za-z0-9_-]{43}$/)

  // Quickstart 2 and 3: the call from the guide (no cookies: the request fixture is a separate client)
  const me = await request.get('/api/public/v1/me', { headers: { Authorization: `Bearer ${secret}` } })
  expect(me.status()).toBe(200)
  expect(await me.text()).toContain('int-walk-rte@example.com')
  const list = await request.get('/api/public/v1/releases?limit=5', { headers: { Authorization: `Bearer ${secret}` } })
  expect(list.status()).toBe(200)
  const page1 = await list.json() as { data: unknown[]; page: { limit: number; hasMore: boolean } }
  expect(Array.isArray(page1.data)).toBe(true)
  expect(page1.page.limit).toBe(5)
  expect((await request.get('/api/public/v1/me')).status()).toBe(401)   // no token, no call

  // Quickstart 4 and 5: add a webhook, send a test event, find it in the delivery log
  await tab(page, 'Webhooks').click()
  await page.getByRole('button', { name: 'Add webhook' }).first().click()
  const w = page.getByRole('form', { name: 'Add webhook' })
  await w.getByLabel('Name').fill(hookName)
  await w.getByLabel('Address').fill(`https://hooks-walk-${stamp}.example.test/releaseos/walk`)
  await w.getByLabel('Events').selectOption(['gate.failed'])
  await w.getByRole('button', { name: 'Add webhook' }).click()
  expect((await page.getByTestId('webhook-secret').innerText()).trim()).toMatch(/^whsec_[A-Za-z0-9_-]{43}$/)
  const id = await page.evaluate(async n => ((await (await fetch('/api/v1/webhooks')).json()) as { subscriptions: { id: string; name: string }[] }).subscriptions.find(s => s.name === n)!.id, hookName)
  try {
    await page.getByRole('group', { name: 'Webhook detail' }).getByRole('button', { name: 'Send test event' }).click()
    await expect(page.getByRole('tab', { name: /^Delivery log/ })).toHaveAttribute('aria-selected', 'true')
    const log = page.getByRole('table', { name: 'Deliveries' })
    await expect(log.locator('tbody tr', { hasText: 'webhook.ping' }).first()).toBeVisible()
    await log.locator('tbody tr', { hasText: 'webhook.ping' }).first().click()
    await expect(page.getByRole('group', { name: 'Delivery detail' })).toContainText('Body that was signed')
    await expect(page.locator('pre[aria-label="Body that was signed"]')).toContainText('"type": "webhook.ping"')

    // the overview now counts the webhook, with its health in words
    await tab(page, 'Overview').click()
    await expect(page.getByRole('table', { name: 'Webhook delivery health' }).locator('tbody tr', { hasText: hookName }).locator('.os-chip')).toHaveText(/^(Healthy|Queued|Failing)$/)
  } finally { await dropWebhook(page, id) }

  // revoke the token (asks first), then the call from the guide is refused
  await tab(page, 'API tokens').click()
  await page.getByRole('table', { name: 'API tokens' }).locator('tbody tr', { hasText: tokenName }).click()
  await page.getByRole('group', { name: 'Token detail' }).getByRole('button', { name: 'Revoke' }).click()
  await page.getByRole('dialog', { name: `Revoke ${tokenName}?` }).getByRole('button', { name: 'Revoke token' }).click()
  await expect(page.getByRole('table', { name: 'API tokens' }).locator('tbody tr', { hasText: tokenName }).locator('.os-chip')).toHaveText('Revoked')
  expect((await request.get('/api/public/v1/me', { headers: { Authorization: `Bearer ${secret}` } })).status()).toBe(401)
})

test('the overview says what it counts, in words and numbers, with the page heading, the four tabs and the breadcrumb', async ({ page }) => {
  await signIn(page, 'int-overview-rm@example.com', 'ReleaseManager')
  await openIntegrations(page)
  await expect(page).toHaveTitle('Integrations · ReleaseOS')
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toContainText('Integrations')
  await expect(page.getByRole('tablist', { name: 'Integrations sections' }).getByRole('tab')).toHaveText(['Overview', 'API tokens', 'Webhooks', 'Developer docs'])
  for (const label of ['Active API tokens', 'Active webhooks', 'Delivery health', 'Next token expiry']) await expect(page.getByRole('group', { name: label })).toBeVisible()
  await expect(page.getByRole('table', { name: 'Webhook delivery health' }).or(page.locator('.os-state-title', { hasText: 'No webhooks yet' }))).toBeVisible()
})

test('a dead-lettered or expiring thing is called out in words with a way to the tab that fixes it (the overview with data)', async ({ page }) => {
  const name = `E2E expiring ${stamp}`
  await signIn(page, 'int-expiring-rte@example.com', 'RTE')
  const made = await page.evaluate(async n => {
    const r = await fetch('/api/v1/api-tokens', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: n, scopes: ['read'], expiresAt: new Date(Date.now() + 3 * 86_400_000).toISOString() }) })
    if (!r.ok) throw new Error(`token failed: ${r.status} ${await r.text()}`)
    return await r.json() as { token: { id: string; version: number } }
  }, name)
  try {
    await openIntegrations(page)
    const n = page.getByRole('region', { name: /tokens? expires? within 14 days/ }).or(page.getByText(/tokens? expires? within 14 days/))
    await expect(n.first()).toBeVisible()
    await page.getByRole('button', { name: 'Open API tokens' }).click()
    await expect(tab(page, 'API tokens')).toHaveAttribute('aria-selected', 'true')
    await expect(page.getByRole('table', { name: 'API tokens' }).locator('tbody tr', { hasText: name })).toBeVisible()
  } finally {
    await page.evaluate(async t => { await fetch(`/api/v1/api-tokens/${t.id}:revoke`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': String(t.version) }, body: '{}' }) }, made.token)
  }
})

test('Developer docs: the first call, the steps, the document list and the OpenAPI document that the page links to', async ({ page, request }) => {
  await signIn(page, 'int-docs-rte@example.com', 'RTE')
  await openIntegrations(page)
  await tab(page, 'Developer docs').click()
  const pre = page.locator('pre[aria-label="Example request"]')
  await expect(pre).toContainText('Authorization: Bearer $RELEASEOS_TOKEN')
  await expect(pre).toContainText(`${new URL(page.url()).origin}/api/public/v1/me`)
  await expect(pre).not.toContainText(/rt_[A-Za-z0-9_-]{20,}/)
  const docs = page.getByRole('list', { name: 'Documents' })
  for (const p of ['docs/DEVELOPERS.md', 'docs/API_VERSIONING.md', 'docs/WEBHOOKS.md', 'docs/samples/webhooks/']) await expect(docs).toContainText(p)
  const href = await page.getByRole('link', { name: /OpenAPI document/ }).getAttribute('href')
  expect(href).toBe('/api/public/v1/openapi.json')
  const spec = await request.get(href!)
  expect(spec.status()).toBe(200)
  expect((await spec.json() as { openapi: string }).openapi).toMatch(/^3\.1/)
})

test('the tabs work from the keyboard (arrows, Home, End) and there is no checkbox and no switch on any of them', async ({ page }) => {
  await signIn(page, 'int-keys-rte@example.com', 'RTE')
  await openIntegrations(page)
  await tab(page, 'Overview').focus()
  await page.keyboard.press('ArrowRight')
  await expect(tab(page, 'API tokens')).toHaveAttribute('aria-selected', 'true')
  await page.keyboard.press('End')
  await expect(tab(page, 'Developer docs')).toHaveAttribute('aria-selected', 'true')
  await page.keyboard.press('Home')
  await expect(tab(page, 'Overview')).toHaveAttribute('aria-selected', 'true')
  for (const t of ['Overview', 'API tokens', 'Webhooks', 'Developer docs']) {
    await tab(page, t).click()
    await expect(page.locator('input[type=checkbox], input[type=radio], [role=switch]'), t).toHaveCount(0)
  }
})

for (const [role, email] of [['Viewer', 'int-viewer@example.com'], ['GovernanceOfficer', 'int-gov@example.com']] as const) {
  test(`a ${role} has no Integrations item, and /integrations says they cannot, without calling the API`, async ({ page }) => {
    const calls: string[] = []
    page.on('request', r => { const p = new URL(r.url()).pathname; if (p.startsWith('/api/v1/api-tokens') || p.startsWith('/api/v1/webhooks')) calls.push(`${r.method()} ${p}`) })
    await signIn(page, email, role)
    await expect(primaryNav(page).getByRole('link', { name: 'Integrations' })).toHaveCount(0)
    await page.goto('/integrations')
    await expect(page.getByRole('heading', { level: 1, name: 'Integrations' })).toBeVisible()
    await expect(page.getByText('You cannot manage integrations')).toBeVisible()
    await expect(page.getByText('Release Train Engineers and Release Managers')).toBeVisible()
    await expect(page.getByRole('tablist')).toHaveCount(0)
    await page.waitForTimeout(500)
    expect(calls, 'the API must not be called for this role').toEqual([])
  })
}

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`axe, ${scheme} theme`, () => {
    test.use({ colorScheme: scheme })
    test(`every tab of Integrations has no serious or critical violation (${scheme})`, async ({ page }) => {
      await signIn(page, `int-axe-${scheme}@example.com`, 'RTE')
      await openIntegrations(page)
      await scan(page, `Integrations, overview (${scheme})`)
      await tab(page, 'API tokens').click()
      await expect(page.getByRole('button', { name: 'Make token' }).first()).toBeVisible()
      await scan(page, `Integrations, API tokens (${scheme})`)
      await page.getByRole('button', { name: 'Make token' }).first().click()
      await expect(page.getByRole('form', { name: 'Make token' })).toBeVisible()
      await scan(page, `Integrations, make token form (${scheme})`)
      await tab(page, 'Webhooks').click()
      await expect(page.getByRole('button', { name: /Add webhook/ }).first()).toBeVisible()
      await scan(page, `Integrations, webhooks (${scheme})`)
      await tab(page, 'Developer docs').click()
      await expect(page.locator('pre[aria-label="Example request"]')).toBeVisible()
      await scan(page, `Integrations, developer docs (${scheme})`)
    })
  })
}
