import AxeBuilder from '@axe-core/playwright'
import { expect, test, type APIRequestContext, type Page, type Playwright } from '@playwright/test'
import { primaryNav, signIn, trainByTitle } from './support'

// REOS-140 and REOS-100: the Deployments list and the monitoring page for one deployment. The scenario is made through the real APIs, as a deployment tool would:
// an RTE creates a service, an environment with thresholds and an ingest key; the key then pushes a canary deployment, its events and its metric samples.
// Nothing here changes the demo trains' own data.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad.map(v => `${v.id} (${v.impact}): ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(' | ')}`), `axe violations on ${where}`).toEqual([])
}

async function json(page: Page, method: string, url: string, body?: unknown) {
  return page.evaluate(async ([m, u, b]) => {
    const r = await fetch(u as string, { method: m as string, headers: { 'Content-Type': 'application/json' }, body: b === undefined ? undefined : JSON.stringify(b) })
    return { status: r.status, body: await r.json().catch(() => null) }
  }, [method, url, body] as const)
}

interface Scenario { externalId: string; env: string; service: string; serviceName: string; version: string; id: string; tool: APIRequestContext; releaseKey: string | null }

/** Service, environment, ingest key, a Running canary at 25 % with `events` events and 20 minutes of samples of both series (the canary a little slower than stable, all under the threshold). */
async function scenario(page: Page, playwright: Playwright, tag: string, events = 30): Promise<Scenario> {
  const stamp = `${tag}-${Date.now()}`
  const service = `chk-${stamp}`, serviceName = `Checkout ${tag}`, env = `Prod EU ${stamp}`, externalId = `dep-${stamp}`, version = 'v3.18.0'
  expect([200, 201]).toContain((await json(page, 'POST', '/api/v1/services', { serviceKey: service, name: serviceName })).status)
  const e = await json(page, 'POST', '/api/v1/environments', { name: env, kind: 'Production', region: 'eu-west-1', errorRateMaxPct: 0.5, latencyP95MaxMs: 300, availabilityMinPct: 99.5, sloAvailabilityPct: 99.9, thresholdForSeconds: 120 })
  expect([200, 201], JSON.stringify(e.body)).toContain(e.status)
  const key = await json(page, 'POST', '/api/v1/ingest-keys', { name: `e2e ${stamp}` })
  expect([200, 201], JSON.stringify(key.body)).toContain(key.status)
  const secret = (key.body as { secret: string }).secret
  const tool = await playwright.request.newContext({ baseURL: new URL(page.url()).origin, extraHTTPHeaders: { Authorization: `Bearer ${secret}` } })   // no cookie jar: the ingest routes refuse a session

  const train = await trainByTitle(page, 'Card portal')
  const rel = await json(page, 'GET', `/api/v1/trains/${train.id}/release`)
  const releaseKey = (rel.body as { key?: string } | null)?.key ?? null
  const now = Date.now(), at = (minutesAgo: number) => new Date(now - minutesAgo * 60000).toISOString().replace(/\.\d{3}Z$/, 'Z')
  const d = await tool.post('/api/v1/ingest/deployments', { data: { externalId, serviceKey: service, environment: env, releaseVersion: version, strategy: 'Canary', canarySteps: [5, 25, 50, 100], state: 'Running', trafficPercent: 25, readyPods: 24, desiredPods: 96, releaseKey, startedAt: at(20) } })
  expect(d.status(), await d.text()).toBe(201)
  const id = ((await d.json()) as { id: string }).id
  const batch = Array.from({ length: events }, (_, i) => ({ level: i % 10 === 9 ? 'Warning' : 'Info', message: `Event ${i + 1}: ${i % 2 ? 'health checks passed' : 'traffic shifted'}`, occurredAt: at(20 - (i * 19) / events), externalId: `ev-${i}` }))
  expect((await tool.post('/api/v1/ingest/events', { data: { deploymentExternalId: externalId, events: batch } })).status()).toBe(200)
  const samples = Array.from({ length: 20 }, (_, i) => {
    const t = at(20 - i)
    return [
      { series: 'Canary', at: t, errorRatePct: 0.06 + (i % 5) * 0.01, latencyP95Ms: 184 + (i % 4) * 3, availabilityPct: 99.96, requestCount: 1000 },
      { series: 'Stable', at: t, errorRatePct: 0.05 + (i % 3) * 0.01, latencyP95Ms: 196 + (i % 3) * 2, availabilityPct: 99.97, requestCount: 9000 },
    ]
  }).flat()
  const m = await tool.post('/api/v1/ingest/metrics', { data: { deploymentExternalId: externalId, samples } })
  expect(m.status(), await m.text()).toBe(200)
  return { externalId, env, service, serviceName, version, id, tool, releaseKey }
}

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('the list finds a deployment, filters it, opens it from its key, and passes axe; the monitoring page shows the cards, charts and events and passes axe', async ({ page, playwright }) => {
      await signIn(page, `deploy-${scheme}@example.com`)
      const s = await scenario(page, playwright, `l-${scheme}`)

      await primaryNav(page).getByRole('link', { name: 'Deployments' }).click()
      await expect(page).toHaveURL(/\/deployments$/)
      await expect(page.getByRole('heading', { level: 1, name: 'Deployments' })).toBeVisible()
      const table = page.getByRole('table', { name: 'Deployments' })
      const row = table.getByRole('row').filter({ hasText: s.serviceName })
      await expect(row).toHaveCount(1)
      for (const cell of [s.env, s.version, 'Running']) await expect(row).toContainText(cell)
      if (s.releaseKey) await expect(row).toContainText(s.releaseKey)

      // filters: the search narrows, the state filter can empty the table, the environment filter keeps it
      await page.getByRole('search', { name: 'Filter deployments' }).getByRole('searchbox').fill(s.service)
      await expect(table.locator('tbody tr')).toHaveCount(1)
      await page.getByLabel('State').selectOption('Failed')
      await expect(table).toContainText('No deployment matches these filters.')
      await page.getByLabel('State').selectOption('')
      await page.getByLabel('Environment').selectOption({ label: s.env })
      await expect(table.locator('tbody tr')).toHaveCount(1)
      await expect(table.locator('tbody tr')).toContainText(/\d+ of \d+|Running/)
      await scan(page, `the deployments list (${scheme})`)

      // the DEP key opens it, keyboard first: focus the key link and press Enter
      await row.getByRole('link', { name: /^Open DEP-/ }).focus()
      await page.keyboard.press('Enter')
      await expect(page).toHaveURL(new RegExp(`/deployments/${s.id}$`))
      await expect(page.getByRole('heading', { level: 1, name: 'Deployment monitoring' })).toBeVisible()
      await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toContainText(/DEP-[0-9A-Z]{6}/)

      // context row, four cards with their words
      await expect(page.getByLabel('Environment')).toHaveValue(/.+/)
      await expect(page.getByRole('group', { name: 'Canary traffic', exact: true })).toContainText('25%')
      await expect(page.getByRole('group', { name: 'Canary traffic', exact: true })).toContainText('24 / 96 pods')
      await expect(page.getByRole('group', { name: 'Error rate', exact: true })).toContainText('rollback threshold')
      await expect(page.getByRole('group', { name: 'Error rate', exact: true })).toContainText('Below 0.5%')
      await expect(page.getByRole('group', { name: 'p95 latency', exact: true })).toContainText('vs. stable version')
      await expect(page.getByRole('group', { name: 'Availability', exact: true })).toContainText('SLO 99.9%')
      await expect(page.getByText(/^LIVE · UPDATED \d\d:\d\d:\d\d UTC$/)).toBeVisible()
      await expect(page.getByText('Automatic rollback is off')).toBeVisible()   // no webhook on this environment, so it cannot be armed: the banner says so

      // charts: threshold line, canary window, an accessible name, a tooltip on hover and on focus, and the data behind them
      const charts = page.getByRole('group', { name: /^Request (error rate|latency p95):/ })
      await expect(charts).toHaveCount(2)
      await expect(page.locator('.dp-threshold')).toHaveCount(2)
      await expect(page.locator('.dp-canary-window')).toHaveCount(2)
      await expect(page.getByText('Rollback threshold 0.5%')).toBeVisible()
      await expect(page.getByText('Rollback threshold 300 ms')).toBeVisible()
      const plot = page.locator('.dp-plot').first()
      await plot.locator('rect').hover()
      await expect(page.locator('.dp-readout').first()).toContainText(/\d\d:\d\d:\d\d UTC · Canary · v3\.18\.0 0\.\d+% · Stable 0\.\d+% · threshold 0\.5%/)
      await page.mouse.move(0, 0)
      await plot.focus()
      const first = await page.locator('.dp-readout').first().innerText()
      await page.keyboard.press('ArrowLeft')
      await expect(page.locator('.dp-readout').first()).not.toHaveText(first)
      await page.getByText('View data').first().click()
      await expect(page.getByRole('table', { name: /Request error rate: samples in the range \(UTC\)/ }).locator('tbody tr')).not.toHaveCount(0)

      // the table of this release's services in this environment, and the events panel
      await expect(page.getByRole('table', { name: 'Service rollout' })).toContainText(s.serviceName)
      const events = page.getByRole('region', { name: 'Deployment events (scrollable)' })
      await expect(events).toContainText('Event 30:')
      await expect(page.getByText('STREAM CONNECTED')).toBeVisible()
      await scan(page, `the deployment monitoring page (${scheme})`)

      // Back returns to the list; the sidebar item does too
      await page.goBack()
      await expect(page.getByRole('heading', { level: 1, name: 'Deployments' })).toBeVisible()
      await page.goForward()
      await expect(page.getByRole('heading', { level: 1, name: 'Deployment monitoring' })).toBeVisible()
      await primaryNav(page).getByRole('link', { name: 'Deployments' }).click()
      await expect(page.getByRole('heading', { level: 1, name: 'Deployments' })).toBeVisible()
      await s.tool.dispose()
    })
  })
}

test('live: a paused state, a new event and a new sample arrive over SignalR; the event stream follows the newest event until the person scrolls back, then keeps their place', async ({ page, playwright }) => {
  await signIn(page, 'deploy-live@example.com')
  const s = await scenario(page, playwright, 'live', 40)
  await page.goto(`/deployments/${s.id}`)
  await expect(page.getByRole('heading', { level: 1, name: 'Deployment monitoring' })).toBeVisible()
  await expect(page.getByText('STREAM CONNECTED')).toBeVisible()
  const log = page.getByRole('region', { name: 'Deployment events (scrollable)' })
  const scrolled = () => log.evaluate(el => ({ top: el.scrollTop, bottom: el.scrollHeight - el.scrollTop - el.clientHeight, scrolls: el.scrollHeight > el.clientHeight }))
  await expect.poll(async () => (await scrolled()).scrolls).toBe(true)
  expect((await scrolled()).bottom).toBeLessThan(5)   // opened at the newest event

  // following: a new event is shown and the log stays at the bottom
  const send = (message: string, ext: string) => s.tool.post('/api/v1/ingest/events', { data: { deploymentExternalId: s.externalId, events: [{ level: 'Info', message, occurredAt: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'), externalId: ext }] } })
  expect((await send('Follow me', 'live-1')).status()).toBe(200)
  await expect(log).toContainText('Follow me')
  await expect.poll(async () => (await scrolled()).bottom).toBeLessThan(5)

  // scrolled back: the position is kept and the footer counts what arrived
  await log.evaluate(el => { el.scrollTop = 0 })
  await expect(page.getByRole('button', { name: 'Resume auto-scroll' })).toBeVisible()
  expect((await send('Not yet seen', 'live-2')).status()).toBe(200)
  await expect(log).toContainText('Not yet seen')
  await expect(page.getByRole('button', { name: 'Resume auto-scroll · 1 new' })).toBeVisible()
  expect((await scrolled()).top).toBe(0)
  await page.getByRole('button', { name: 'Resume auto-scroll · 1 new' }).click()
  await expect.poll(async () => (await scrolled()).bottom).toBeLessThan(5)
  await page.getByRole('button', { name: 'Pause auto-scroll' }).click()
  await expect(page.getByRole('button', { name: 'Resume auto-scroll' })).toBeVisible()

  // the tool reports Paused: the whole page reflects it without a reload
  const paused = await s.tool.post('/api/v1/ingest/deployments', { data: { externalId: s.externalId, state: 'Paused' } })
  expect(paused.status(), await paused.text()).toBe(200)
  await expect(page.getByText('Rollout paused')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Resume rollout' })).toBeVisible()
  await expect(page.getByRole('table', { name: 'Service rollout' })).toContainText('Paused')
  await expect(page.locator('.os-chip', { hasText: 'Paused' }).first()).toBeVisible()
  await s.tool.dispose()
})

test('Rollback and Pause rollout ask first; Rollback needs a reason, sends the confirmed call with the row version, and a failure is shown; a Viewer sees why they are disabled', async ({ page, playwright, browser }) => {
  await signIn(page, 'deploy-control@example.com')
  const s = await scenario(page, playwright, 'ctl', 3)
  await page.goto(`/deployments/${s.id}`)
  await expect(page.getByRole('heading', { level: 1, name: 'Deployment monitoring' })).toBeVisible()

  // Escape cancels and focus returns to the button; the dialog starts on Cancel
  const rollback = page.getByRole('button', { name: 'Rollback' })
  await rollback.click()
  const dialog = page.getByRole('dialog', { name: /^Roll back DEP-/ })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
  await expect(rollback).toBeFocused()

  // a reason is required before anything is sent
  let sent = 0
  await page.route('**/deployments/*:rollback', route => { sent++; void route.continue() })
  await rollback.click()
  await dialog.getByRole('button', { name: 'Roll back' }).click()
  await expect(dialog.getByRole('alert')).toContainText('Say why you are rolling back')
  expect(sent).toBe(0)

  // the real call: the environment has no control webhook, so the server refuses with its reason and the dialog shows it (nothing is rolled back)
  await dialog.getByLabel('Reason (required)').fill('Error budget burning on the canary')
  await dialog.getByRole('button', { name: 'Roll back' }).click()
  await expect(dialog.getByRole('alert')).toContainText('no control webhook')
  expect(sent).toBe(1)
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await page.unroute('**/deployments/*:rollback')

  // an accepted call (the receiver is stubbed here; the signed call itself is covered by the API tests): the request carries confirm, the reason and If-Match
  let seen: { body: string; ifMatch: string | undefined } | null = null
  await page.route('**/deployments/*:rollback', route => {
    seen = { body: route.request().postData() ?? '', ifMatch: route.request().headers()['if-match'] }
    void route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: 'a', deploymentId: s.id, action: 'Rollback', source: 'Manual', requestedByName: 'Control', requestedAt: new Date().toISOString(), reason: 'x', breachId: null, status: 'Succeeded', httpStatus: 200, error: null, completedAt: null, version: 1 }) })
  })
  await rollback.click()
  await dialog.getByLabel('Reason (required)').fill('Roll it back')
  await dialog.getByRole('button', { name: 'Roll back' }).click()
  await expect(dialog).toBeHidden()
  expect(JSON.parse(seen!.body)).toEqual({ confirm: true, reason: 'Roll it back' })
  expect(seen!.ifMatch).toMatch(/^\d+$/)

  // Pause rollout asks as well, and a failed call to the tool is shown, not swallowed
  await page.route('**/deployments/*:pause', route => void route.fulfill({ status: 502, contentType: 'application/json', body: JSON.stringify({ guard: 'ControlFailed', message: 'The pause call did not succeed: timeout', action: null }) }))
  await page.getByRole('button', { name: 'Pause rollout' }).click()
  const pause = page.getByRole('dialog', { name: /^Pause DEP-/ })
  await expect(pause).toBeVisible()
  await pause.getByRole('button', { name: 'Pause rollout' }).click()
  await expect(page.getByText('The call to the deployment tool did not succeed')).toBeVisible()
  await expect(page.getByText(/The pause call did not succeed: timeout/)).toBeVisible()
  await scan(page, 'the monitoring page with a control failure')

  // a Viewer can watch but not act, and the page says why
  const ctx = await browser.newContext()
  const viewer = await ctx.newPage()
  await signIn(viewer, 'deploy-viewer@example.com', 'Viewer')
  await viewer.goto(`/deployments/${s.id}`)
  await expect(viewer.getByRole('heading', { level: 1, name: 'Deployment monitoring' })).toBeVisible()
  await expect(viewer.getByRole('button', { name: 'Rollback' })).toBeDisabled()
  await expect(viewer.getByRole('button', { name: 'Pause rollout' })).toBeDisabled()
  await expect(viewer.getByRole('button', { name: 'Rollback' })).toHaveAccessibleDescription(/Only an RTE or a Release Manager/)
  await ctx.close()
  await s.tool.dispose()
})

test('when the live channel is unavailable the page says so and still shows the last data, with a way to refresh', async ({ page, playwright }) => {
  await signIn(page, 'deploy-offline@example.com')
  const s = await scenario(page, playwright, 'off', 3)
  await page.route('**/hub/**', route => void route.abort())   // the negotiate call fails: the shell's live state goes offline
  await page.goto(`/deployments/${s.id}`)
  await expect(page.getByRole('heading', { level: 1, name: 'Deployment monitoring' })).toBeVisible()
  await expect(page.getByText('Live updates are offline').first()).toBeVisible()
  await expect(page.getByText('STREAM DISCONNECTED')).toBeVisible()
  await expect(page.getByText(/^OFFLINE · NOT UPDATING$/)).toBeVisible()
  await expect(page.getByRole('group', { name: 'Canary traffic', exact: true })).toContainText('25%')
  await expect(page.getByRole('button', { name: 'Refresh' })).toBeVisible()
  await scan(page, 'the monitoring page while offline')
  await s.tool.dispose()
})

test('the environment and time-range selectors drive every panel; an unknown deployment says so', async ({ page, playwright }) => {
  await signIn(page, 'deploy-selectors@example.com')
  const s = await scenario(page, playwright, 'sel', 3)
  // the same service in a second environment (a staging rollout) makes the environment selector useful
  const staging = `Stage EU ${Date.now()}`
  expect([200, 201]).toContain((await json(page, 'POST', '/api/v1/environments', { name: staging, kind: 'Staging', errorRateMaxPct: 1 })).status)
  const other = await s.tool.post('/api/v1/ingest/deployments', { data: { externalId: `${s.externalId}-stage`, serviceKey: s.service, environment: staging, releaseVersion: 'v3.17.9', strategy: 'Rolling', state: 'Successful', readyPods: 4, desiredPods: 4 } })
  expect(other.status(), await other.text()).toBe(201)
  await page.goto(`/deployments/${s.id}`)
  await expect(page.getByRole('heading', { level: 1, name: 'Deployment monitoring' })).toBeVisible()
  await expect(page.getByLabel('Environment').locator('option')).toHaveCount(2)

  // a short range leaves fewer points than a long one: the chart's data table follows the selector
  await page.getByLabel('Time range').selectOption('15')
  await page.getByText('View data').first().click()
  const rows = page.getByRole('table', { name: /Request error rate: samples/ }).locator('tbody tr')
  await expect(rows).not.toHaveCount(0)
  const short = await rows.count()
  await page.getByLabel('Time range').selectOption('60')
  await expect.poll(async () => rows.count()).toBeGreaterThan(short)

  // choosing the other environment shows that environment's deployment
  await page.getByLabel('Environment').selectOption({ label: staging })
  await expect(page).not.toHaveURL(new RegExp(`/deployments/${s.id}$`))
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toBeVisible()
  await expect(page.getByRole('group', { name: 'Canary traffic', exact: true })).toContainText('100%')
  await expect(page.locator('.dp-live ~ *, .dp-context').first()).toBeVisible()

  await page.goto('/deployments/does-not-exist')
  await expect(page.getByText('This deployment is not here')).toBeVisible()
  await s.tool.dispose()
})
