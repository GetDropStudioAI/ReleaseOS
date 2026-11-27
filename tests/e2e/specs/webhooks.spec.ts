import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openPage, primaryNav, signIn } from './support'

// REOS-164 (D61): the Webhooks screen (/webhooks, Management group, RTE and Release Manager only). Add (the secret shown once and gone afterwards, the address never shown), the delivery log with a failing
// receiver (retrying, the attempts, the signed body), Replay, Pause and Resume, Rotate secret and Delete behind confirmations, 409 and 422 in words, loading / empty / error / no-permission states,
// no checkbox or switch, and axe in both themes. The receiver is an address that does not resolve, so every delivery fails and is retried: that is the case worth seeing on screen.
// Adds its own webhooks (unique names) to the shared demo database and deletes them again.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now() % 1_000_000
const TOKEN = 'tok3n-e2e-7c1d'
const receiver = (n: string) => `https://hooks-${n}.example.test/releaseos/${TOKEN}`

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  expect(bad, `axe on ${where}: ${bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(', ')}`).join('; ')}`).toEqual([])
}

const table = (page: Page) => page.getByRole('table', { name: 'Webhooks' })
const row = (page: Page, text: string | RegExp) => table(page).locator('tbody tr:not(:has(.os-table-empty))', { hasText: text })
const form = (page: Page, name: string) => page.getByRole('form', { name })
const detail = (page: Page) => page.getByRole('group', { name: 'Webhook detail' })
const secretOf = async (page: Page) => (await page.getByTestId('webhook-secret').innerText()).trim()
const deliveries = (page: Page) => page.getByRole('table', { name: 'Deliveries' })

async function openWebhooks(page: Page) {
  await openPage(page, '/webhooks')
  await expect(page.getByRole('heading', { level: 1, name: 'Webhooks' })).toBeVisible()
  await expect(table(page).or(page.getByText('No webhooks yet'))).toBeVisible()
}

interface Made { subscription: { id: string; version: number; name: string }; secret: string }
/** A webhook through the API as the signed-in person. */
async function hook(page: Page, name: string, url: string, events = ['gate.failed']): Promise<Made> {
  return page.evaluate(async ([n, u, e]) => {
    const r = await fetch('/api/v1/webhooks', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: n, url: u, eventTypes: e }) })
    if (!r.ok) throw new Error(`webhook failed: ${r.status} ${await r.text()}`)
    return await r.json() as Made
  }, [name, url, events] as const)
}
/** Removes a webhook the test made, whatever version it is at now. */
async function drop(page: Page, id: string) {
  await page.evaluate(async i => {
    const o = await (await fetch('/api/v1/webhooks')).json() as { subscriptions: { id: string; version: number }[] }
    const v = o.subscriptions.find(s => s.id === i)?.version
    if (v !== undefined) await fetch(`/api/v1/webhooks/${i}`, { method: 'DELETE', headers: { 'If-Match': String(v) } })
  }, id)
}

test('the sidebar has Integrations in the Management group for an RTE; Webhooks are its tab and the page /webhooks still opens', async ({ page }) => {
  await signIn(page, 'wh-nav-rte@example.com', 'RTE')
  await primaryNav(page).getByRole('link', { name: 'Integrations' }).click()
  await expect(page).toHaveURL(/\/integrations$/)
  await page.getByRole('tab', { name: 'Webhooks', exact: true }).click()
  await expect(page.getByRole('button', { name: /^(Add webhook)$/ }).first()).toBeVisible()
  const names = await primaryNav(page).locator('.os-nav-group-management a').allInnerTexts()
  expect(names.map(n => n.trim())).toContain('Service catalog')
  expect(names.map(n => n.trim())).toContain('Integrations')
  await page.goto('/webhooks')
  await expect(page.getByRole('heading', { level: 1, name: 'Webhooks' })).toBeVisible()
  await expect(page).toHaveTitle('Webhooks · ReleaseOS')
  await expect(page.getByText('This screen is not built yet')).toHaveCount(0)
})

test('a Release Manager adds a webhook: the secret is shown once and is gone when the panel closes and after a reload; the address is never shown', async ({ page }) => {
  const name = `E2E add ${stamp}`
  await signIn(page, 'wh-add-rm@example.com', 'ReleaseManager')
  await openWebhooks(page)
  await page.getByRole('button', { name: 'Add webhook' }).first().click()
  const f = form(page, 'Add webhook')
  // not complete: the button is off and says why, in words
  await expect(f.getByRole('button', { name: 'Add webhook' })).toBeDisabled()
  await expect(f.getByText('Enter a name.')).toBeAttached()
  await f.getByLabel('Name').fill(name)
  await f.getByLabel('Address').fill(receiver('add'))
  await expect(f.getByText('Choose at least one event.')).toBeAttached()
  await f.getByLabel('Events').selectOption(['gate.failed', 'release.created'])
  await f.getByRole('button', { name: 'Add webhook' }).click()

  await expect(page.getByText('Copy this secret now')).toBeVisible()
  const secret = await secretOf(page)
  expect(secret).toMatch(/^whsec_[A-Za-z0-9_-]{43}$/)
  await expect(detail(page)).toContainText('hooks-add.example.test')
  await expect(detail(page)).toContainText('Release created, Gate failed')
  await expect(detail(page)).toContainText('Version 1')
  await expect(page.locator('body')).not.toContainText(TOKEN)   // the address is a secret: only its host is shown

  await page.getByRole('button', { name: 'Copy secret' }).click()
  await expect(row(page, name)).toBeVisible()
  await expect(row(page, name).locator('.os-chip')).toHaveText('Active')
  await expect(row(page, name)).toContainText('Nothing waiting')

  // gone with the panel, with a reload, and from every store
  await page.getByRole('button', { name: new RegExp(`^Close ${name}`) }).click()
  await expect(page.getByTestId('webhook-secret')).toHaveCount(0)
  await row(page, name).click()
  await expect(page.getByTestId('webhook-secret')).toHaveCount(0)
  await page.reload()
  await expect(row(page, name)).toBeVisible()
  await expect(page.locator('body')).not.toContainText(secret)
  const stores = await page.evaluate(() => JSON.stringify([{ ...localStorage }, { ...sessionStorage }, document.cookie]))
  expect(stores).not.toContain('whsec_')
  expect(await page.evaluate(() => performance.getEntriesByType('navigation').map(e => (e as PerformanceNavigationTiming).name).join(' '))).not.toContain('whsec_')
  await row(page, name).click()
  await detail(page).getByRole('button', { name: 'Delete' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Delete webhook' }).click()
  await expect(row(page, name)).toHaveCount(0)
})

test('a refused address is explained in words; the server decides, not the form', async ({ page }) => {
  await signIn(page, 'wh-refuse-rte@example.com', 'RTE')
  await openWebhooks(page)
  await page.getByRole('button', { name: 'Add webhook' }).first().click()
  const f = form(page, 'Add webhook')
  await f.getByLabel('Name').fill('Refused')
  await f.getByLabel('Address').fill('http://hooks.example.test/in')
  await f.getByLabel('Events').selectOption(['*'])
  await expect(f.getByText('The address must start with https://.')).toBeAttached()
  await f.getByLabel('Address').fill('https://127.0.0.1/in')
  await f.getByRole('button', { name: 'Add webhook' }).click()
  await expect(f.getByRole('alert')).toContainText(/loopback, private, link-local or internal address/i)
  await expect(f.getByLabel('Name')).toHaveValue('Refused')   // the form is kept
})

test('a test event goes through the whole path: it is queued, tried and retried against a receiver that does not answer, with every try and the signed body in the log; Replay sends it again', async ({ page }) => {
  const name = `E2E log ${stamp}`
  await signIn(page, 'wh-log-rte@example.com', 'RTE')
  const made = await hook(page, name, receiver('log'), ['gate.failed'])
  try {
    await openWebhooks(page)
    await row(page, name).click()
    await detail(page).getByRole('button', { name: 'Send test event' }).click()
    await expect(page.getByRole('tab', { name: /^Delivery log/ })).toHaveAttribute('aria-selected', 'true')
    await expect(deliveries(page)).toBeVisible()
    const d = deliveries(page).locator('tbody tr', { hasText: name })
    await expect(d).toHaveCount(1)
    await expect(d).toContainText('webhook.ping')
    // the sender tries it within a few seconds, fails (the host does not resolve) and schedules the next try
    await expect(d.locator('.os-chip')).toHaveText('Retrying', { timeout: 40_000 })
    await expect(d).toContainText('1 attempt')
    await expect(d).toContainText(/does not resolve|HttpRequestException|SocketException/)
    await expect(d).toContainText('Next try')
    await d.click()
    const panel = page.getByRole('group', { name: 'Delivery detail' })
    await expect(panel.getByRole('list', { name: 'Attempts' }).getByRole('listitem')).toHaveCount(1)
    await expect(panel).toContainText('Attempt 1:')
    const body = await panel.getByLabel('Body that was signed').innerText()
    expect(JSON.parse(body).type).toBe('webhook.ping')
    expect(body).not.toContain(TOKEN)
    await expect(panel.getByRole('button', { name: 'Replay' })).toBeDisabled()   // still being tried
    await expect(panel).toContainText('This delivery is still being tried.')

    // a finished delivery needs about 8 hours of retries, so Replay on one is driven by the mocked dead-letter test below and by the API tests; here the pending one refuses it in words
    await page.getByRole('button', { name: 'Close the delivery' }).click()
    await expect(page.getByText('Select a delivery')).toBeVisible()
    await page.getByRole('combobox', { name: 'Status' }).selectOption('Delivered')
    await expect(page.getByText('Nothing matches these filters.')).toBeVisible()
    await page.getByRole('combobox', { name: 'Status' }).selectOption('')
    await page.getByRole('combobox', { name: 'Webhook' }).selectOption({ label: name })
    await expect(deliveries(page).locator('tbody tr')).toHaveCount(1)
  } finally { await drop(page, made.subscription.id) }
})

test('pause holds the webhook and says so; Send test event is off with the reason; Resume brings it back', async ({ page }) => {
  const name = `E2E pause ${stamp}`
  await signIn(page, 'wh-pause-rte@example.com', 'RTE')
  const made = await hook(page, name, receiver('pause'))
  try {
    await openWebhooks(page)
    await row(page, name).click()
    await detail(page).getByRole('button', { name: 'Pause' }).click()
    await expect(row(page, name).locator('.os-chip')).toHaveText('Paused')
    await expect(detail(page).getByRole('button', { name: 'Resume' })).toBeVisible()
    await expect(detail(page).getByRole('button', { name: 'Send test event' })).toBeDisabled()
    await expect(detail(page).getByText('Resume the webhook first.')).toBeAttached()
    await detail(page).getByRole('button', { name: 'Resume' }).click()
    await expect(row(page, name).locator('.os-chip')).toHaveText('Active')
  } finally { await drop(page, made.subscription.id) }
})

test('rotate secret asks first (starts on Cancel, Escape cancels), then shows the new secret once and the version goes up', async ({ page }) => {
  const name = `E2E rotate ${stamp}`
  await signIn(page, 'wh-rotate-rte@example.com', 'RTE')
  const made = await hook(page, name, receiver('rotate'))
  try {
    await openWebhooks(page)
    await row(page, name).click()
    await detail(page).getByRole('button', { name: 'Rotate secret' }).click()
    const dlg = page.getByRole('dialog')
    await expect(dlg).toContainText('stops verifying at once')
    await expect(dlg.getByRole('button', { name: 'Cancel' })).toBeFocused()
    await page.keyboard.press('Escape')
    await expect(dlg).toHaveCount(0)
    await expect(detail(page)).toContainText('Version 1')
    await detail(page).getByRole('button', { name: 'Rotate secret' }).click()
    await dlg.getByRole('button', { name: 'Rotate secret' }).click()
    await expect(page.getByText('Copy this secret now')).toBeVisible()
    await expect(page.getByText('The secret was rotated and the old one has stopped verifying.')).toBeVisible()
    const second = await secretOf(page)
    expect(second).toMatch(/^whsec_/)
    expect(second).not.toBe(made.secret)
    await expect(detail(page)).toContainText('Version 2, rotated')
  } finally { await drop(page, made.subscription.id) }
})

test('delete asks first (starts on Cancel), removes the webhook and its log, and the last one leaves the empty state', async ({ page }) => {
  const name = `E2E delete ${stamp}`
  await signIn(page, 'wh-delete-rte@example.com', 'RTE')
  const made = await hook(page, name, receiver('delete'))
  await openWebhooks(page)
  await row(page, name).click()
  await detail(page).getByRole('button', { name: 'Delete' }).click()
  const dlg = page.getByRole('dialog')
  await expect(dlg).toContainText('cannot be undone')
  await expect(dlg.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await dlg.getByRole('button', { name: 'Cancel' }).click()
  await expect(row(page, name)).toBeVisible()
  await detail(page).getByRole('button', { name: 'Delete' }).click()
  await dlg.getByRole('button', { name: 'Delete webhook' }).click()
  await expect(row(page, name)).toHaveCount(0)
  const gone = await page.evaluate(async id => (await fetch(`/api/v1/webhooks/deliveries?subscriptionId=${id}`)).json().then((o: { deliveries: unknown[] }) => o.deliveries.length), made.subscription.id)
  expect(gone).toBe(0)   // its log went with it
})

test('a webhook changed by someone else: Pause shows the conflict notice and the list is refreshed; a refused add says why', async ({ page }) => {
  const name = `E2E conflict ${stamp}`
  await signIn(page, 'wh-conflict-rte@example.com', 'RTE')
  const made = await hook(page, name, receiver('conflict'))
  try {
    await openWebhooks(page)
    await row(page, name).click()
    // someone else renames it first (version moves on)
    await page.evaluate(async ([id, v, n]) => {
      const r = await fetch(`/api/v1/webhooks/${id}`, { method: 'PUT', headers: { 'Content-Type': 'application/json', 'If-Match': String(v) }, body: JSON.stringify({ name: `${n} (renamed)`, eventTypes: ['gate.failed'] }) })
      if (!r.ok) throw new Error(`rename failed ${r.status}`)
    }, [made.subscription.id, made.subscription.version, name] as const)
    await detail(page).getByRole('button', { name: 'Pause' }).click()
    await expect(page.getByText('Someone else changed this first')).toBeVisible()
    await expect(row(page, name).locator('.os-chip')).toHaveText('Active')   // nothing was paused
    await page.getByRole('button', { name: 'Use theirs' }).click()
    await expect(page.getByText('Someone else changed this first')).toHaveCount(0)
    await detail(page).getByRole('button', { name: 'Pause' }).click()
    await expect(row(page, `${name} (renamed)`).locator('.os-chip')).toHaveText('Paused')

    await page.route('**/api/v1/webhooks', route => (route.request().method() === 'POST'
      ? route.fulfill({ status: 422, contentType: 'application/json', body: JSON.stringify({ guard: 'WebhookUrlRefused', message: 'The host evil.example.com is not on the allowed list.' }) })
      : route.continue()))
    await page.getByRole('button', { name: 'Add webhook' }).first().click()
    const f = form(page, 'Add webhook')
    await f.getByLabel('Name').fill('Refused')
    await f.getByLabel('Address').fill('https://evil.example.com/in')
    await f.getByLabel('Events').selectOption(['*'])
    await f.getByRole('button', { name: 'Add webhook' }).click()
    await expect(f.getByRole('alert')).toContainText('The host evil.example.com is not on the allowed list.')
  } finally { await drop(page, made.subscription.id) }
})

for (const [role, email] of [['Viewer', 'wh-viewer@example.com'], ['GovernanceOfficer', 'wh-gov@example.com']] as const) {
  test(`a ${role} has no Integrations item, and /webhooks says they cannot, without calling the API`, async ({ page }) => {
    const calls: string[] = []
    page.on('request', r => { if (new URL(r.url()).pathname.startsWith('/api/v1/webhooks')) calls.push(`${r.method()} ${r.url()}`) })
    await signIn(page, email, role)
    await expect(primaryNav(page).getByRole('link', { name: 'Integrations' })).toHaveCount(0)
    await expect(primaryNav(page).getByRole('link', { name: 'Service catalog' })).toBeVisible()
    await page.goto('/webhooks')
    await expect(page.getByRole('heading', { level: 1, name: 'Webhooks' })).toBeVisible()
    await expect(page.getByText('You cannot manage webhooks')).toBeVisible()
    await expect(page.getByText('Release Train Engineers and Release Managers')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Add webhook' })).toHaveCount(0)
    await expect(table(page)).toHaveCount(0)
    await page.waitForTimeout(500)
    expect(calls, 'the API must not be called for this role').toEqual([])
    // and the API itself says no
    const status = await page.evaluate(async () => (await fetch('/api/v1/webhooks')).status)
    expect(status).toBe(403)
  })
}

test('a 403 from the API shows the no-permission page', async ({ page }) => {
  await signIn(page, 'wh-403-rte@example.com', 'RTE')
  await page.route('**/api/v1/webhooks', route => route.fulfill({ status: 403, contentType: 'application/json', body: '{}' }))
  await page.goto('/webhooks')
  await expect(page.getByText('You cannot manage webhooks')).toBeVisible()
})

test('loading, error with Retry, the empty state, and a closed allow-list that turns Add off with the reason', async ({ page }) => {
  await signIn(page, 'wh-states-rte@example.com', 'RTE')
  const overview = (extra: object = {}) => JSON.stringify({
    events: [{ key: 'gate.failed', name: 'Gate failed', description: 'A gate was marked failed.' }], allEvents: '*', allowList: { mode: 'open', hosts: [], allowPrivateTargets: false },
    limits: { maxSubscriptions: 20, maxAttempts: 10, timeoutSeconds: 10, retentionDays: 30, replayWindowSeconds: 300 }, subscriptions: [], ...extra,
  })
  let release: () => void = () => undefined
  const gate = new Promise<void>(r => { release = r })
  await page.route('**/api/v1/webhooks', async route => { await gate; await route.continue() })
  await page.goto('/webhooks')
  await expect(page.getByText('Loading the webhooks…')).toBeVisible()
  release()
  await expect(table(page).or(page.getByText('No webhooks yet'))).toBeVisible()
  await page.unroute('**/api/v1/webhooks')

  let failing = true
  await page.route('**/api/v1/webhooks', route => (failing ? route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'The webhooks are down.' }) }) : route.continue()))
  await page.goto('/webhooks')
  const err = page.locator('.os-notice').filter({ hasText: 'could not be loaded' })
  await expect(err).toContainText('The webhooks are down.')
  failing = false
  await err.getByRole('button', { name: 'Retry' }).click()
  await expect(table(page).or(page.getByText('No webhooks yet'))).toBeVisible()
  await page.unroute('**/api/v1/webhooks')

  await page.route('**/api/v1/webhooks', route => route.fulfill({ status: 200, contentType: 'application/json', body: overview() }))
  await page.goto('/webhooks')
  await expect(page.getByText('No webhooks yet')).toBeVisible()
  await expect(page.getByText('Any public host may receive events')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Add webhook' })).toHaveCount(2)   // the heading action and the empty state's
  await page.unroute('**/api/v1/webhooks')

  await page.route('**/api/v1/webhooks', route => route.fulfill({ status: 200, contentType: 'application/json', body: overview({ allowList: { mode: 'closed', hosts: [], allowPrivateTargets: false } }) }))
  await page.goto('/webhooks')
  await expect(page.getByText('No receiver host is allowed yet', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Add webhook' })).toBeDisabled()
  await expect(page.getByText(/Webhooks:Out:AllowedHosts/).first()).toBeAttached()
})

test('a dead-lettered delivery shows the notice, can be found and replayed from the log', async ({ page }) => {
  await signIn(page, 'wh-dead-rte@example.com', 'RTE')
  const sub = { id: 's1', name: 'Dashboard', host: 'hooks.example.com', eventTypes: ['*'], status: 'Active', secretVersion: 1, hasSecret: true, secretRotatedAt: null, createdAt: '2026-11-26T12:00:00Z', createdBy: 'Rita', lastChangedAt: null, pending: 0, deadLetters: 1, version: 3 }
  const dead = { id: 'd1', subscriptionId: 's1', subscriptionName: 'Dashboard', eventId: 'evt_41', eventType: 'gate.failed', status: 'DeadLetter', attemptCount: 10, nextAttemptAt: null, lastAttemptAt: '2026-11-26T20:30:00Z', lastStatusCode: 503, lastError: 'HTTP 503', deliveredAt: null, finishedAt: '2026-11-26T20:30:00Z', replayOfDeliveryId: null, createdAt: '2026-11-26T12:00:00Z', version: 11 }
  const replayed = { ...dead, id: 'd2', status: 'Pending', attemptCount: 0, nextAttemptAt: '2026-11-26T21:00:00Z', lastStatusCode: null, lastError: null, finishedAt: null, replayOfDeliveryId: 'd1', createdAt: '2026-11-26T21:00:00Z', version: 1 }
  let list = [dead]
  const calls: string[] = []
  await page.route('**/api/v1/webhooks', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
    events: [{ key: 'gate.failed', name: 'Gate failed', description: '' }], allEvents: '*', allowList: { mode: 'restricted', hosts: ['hooks.example.com'], allowPrivateTargets: false },
    limits: { maxSubscriptions: 20, maxAttempts: 10, timeoutSeconds: 10, retentionDays: 30, replayWindowSeconds: 300 }, subscriptions: [sub] }) }))
  await page.route('**/api/v1/webhooks/deliveries?**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ deliveries: list }) }))
  await page.route('**/api/v1/webhooks/deliveries/d1', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ delivery: dead, payloadJson: '{"id":"evt_41","type":"gate.failed"}',
    attempts: [{ attemptNo: 1, attemptedAt: '2026-11-26T12:00:05Z', statusCode: 503, durationMs: 41, error: 'HTTP 503' }, { attemptNo: 10, attemptedAt: '2026-11-26T20:30:00Z', statusCode: 503, durationMs: 38, error: 'HTTP 503' }] }) }))
  await page.route('**/api/v1/webhooks/deliveries/d2', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ delivery: replayed, payloadJson: '{"id":"evt_41","type":"gate.failed"}', attempts: [] }) }))
  await page.route('**/api/v1/webhooks/deliveries/d1:replay', route => { calls.push(route.request().method()); list = [replayed, dead]; return route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify(replayed) }) })
  await page.goto('/webhooks')
  await expect(page.getByText('1 delivery was dead-lettered')).toBeVisible()
  await expect(page.getByText('Allowed: hooks.example.com.')).toBeVisible()
  await page.getByRole('button', { name: 'Show them' }).click()
  const d = deliveries(page).locator('tbody tr')
  await expect(d).toHaveCount(1)
  await expect(d.locator('.os-chip')).toHaveText('Dead letter')
  await expect(d).toContainText('10 attempts')
  await expect(d).toContainText('HTTP 503')
  await d.click()
  const panel = page.getByRole('group', { name: 'Delivery detail' })
  await expect(panel.getByRole('list', { name: 'Attempts' }).getByRole('listitem')).toHaveCount(2)
  await panel.getByRole('button', { name: 'Replay' }).click()
  await expect.poll(() => calls).toEqual(['POST'])
  await expect(deliveries(page).locator('tbody tr')).toHaveCount(2)
  await expect(deliveries(page).locator('tbody tr').first()).toContainText('A replay')
  await expect(page.getByRole('group', { name: 'Delivery detail' })).toContainText('Replay of')
})

test('there is no checkbox and no switch anywhere on the page, and the events picker is a labelled list', async ({ page }) => {
  await signIn(page, 'wh-nocheck-rte@example.com', 'RTE')
  await openWebhooks(page)
  await page.getByRole('button', { name: 'Add webhook' }).first().click()
  await expect(page.locator('input[type=checkbox], input[type=radio], [role=switch]')).toHaveCount(0)
  const picker = form(page, 'Add webhook').getByLabel('Events')
  await expect(picker).toHaveAttribute('multiple', '')
  expect(await picker.locator('option').count()).toBeGreaterThanOrEqual(15)   // All events and the 14 of the catalogue
  await page.getByRole('tab', { name: /^Delivery log/ }).click()
  await expect(page.locator('input[type=checkbox], input[type=radio], [role=switch]')).toHaveCount(0)
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} mode`, () => {
    test.use({ colorScheme: scheme })
    test(`Webhooks: the empty state, the list with a webhook selected, the add form, the one-time secret, the delivery log and the dialogs are axe clean (${scheme})`, async ({ page }) => {
      const name = `E2E a11y ${scheme} ${stamp}`
      await signIn(page, `wh-a11y-${scheme}@example.com`, 'RTE')
      const made = await hook(page, name, receiver(`a11y-${scheme}`), ['gate.failed', 'release.created'])
      try {
        await page.route('**/api/v1/webhooks', route => (route.request().method() === 'GET'
          ? route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [{ key: 'gate.failed', name: 'Gate failed', description: '' }], allEvents: '*', allowList: { mode: 'open', hosts: [], allowPrivateTargets: false },
            limits: { maxSubscriptions: 20, maxAttempts: 10, timeoutSeconds: 10, retentionDays: 30, replayWindowSeconds: 300 }, subscriptions: [] }) }) : route.continue()))
        await page.goto('/webhooks')
        await expect(page.getByText('No webhooks yet')).toBeVisible()
        await scan(page, `Webhooks, the empty state (${scheme})`)
        await page.unroute('**/api/v1/webhooks')

        await openWebhooks(page)
        await scan(page, `Webhooks, the list (${scheme})`)
        await row(page, name).click()
        await expect(detail(page)).toBeVisible()
        await scan(page, `Webhooks, a webhook selected (${scheme})`)

        await detail(page).getByRole('button', { name: 'Edit' }).click()
        await expect(form(page, 'Edit webhook')).toBeVisible()
        await scan(page, `Webhooks, the edit form (${scheme})`)
        await form(page, 'Edit webhook').getByRole('button', { name: 'Cancel' }).click()

        await page.getByRole('button', { name: 'Add webhook' }).first().click()
        await expect(form(page, 'Add webhook')).toBeVisible()
        await scan(page, `Webhooks, the add form with its reason (${scheme})`)
        await form(page, 'Add webhook').getByLabel('Name').fill(`${name} new`)
        await form(page, 'Add webhook').getByLabel('Address').fill(receiver(`a11y-new-${scheme}`))
        await form(page, 'Add webhook').getByLabel('Events').selectOption(['*'])
        await form(page, 'Add webhook').getByRole('button', { name: 'Add webhook' }).click()
        await expect(page.getByText('Copy this secret now')).toBeVisible()
        await scan(page, `Webhooks, the one-time secret (${scheme})`)

        await detail(page).getByRole('button', { name: 'Rotate secret' }).click()
        await expect(page.getByRole('dialog')).toBeVisible()
        await scan(page, `Webhooks, the rotate dialog (${scheme})`)
        await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()
        await detail(page).getByRole('button', { name: 'Delete' }).click()
        await expect(page.getByRole('dialog')).toBeVisible()
        await scan(page, `Webhooks, the delete dialog (${scheme})`)
        await page.getByRole('dialog').getByRole('button', { name: 'Delete webhook' }).click()
        await expect(row(page, `${name} new`)).toHaveCount(0)

        await row(page, name).click()
        await detail(page).getByRole('button', { name: 'Send test event' }).click()
        await expect(deliveries(page).locator('tbody tr', { hasText: name })).toHaveCount(1)
        await scan(page, `Webhooks, the delivery log (${scheme})`)
        await deliveries(page).locator('tbody tr', { hasText: name }).click()
        await expect(page.getByRole('group', { name: 'Delivery detail' })).toBeVisible()
        await scan(page, `Webhooks, a delivery selected (${scheme})`)
      } finally { await drop(page, made.subscription.id) }
    })
  })
}
