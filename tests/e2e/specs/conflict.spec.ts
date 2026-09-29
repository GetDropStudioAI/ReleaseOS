import { expect, test } from '@playwright/test'
import { signIn, trainByTitle } from './support'

// REOS-27: two people editing one thing. The loser sees a visible 409 inline (no modal), keeps their draft and chooses what happens next.

test('a stale gate action shows who changed it and what it is now, inline', async ({ browser }) => {
  const a = await (await browser.newContext()).newPage()
  const b = await (await browser.newContext()).newPage()
  // B never hears the live push, which is exactly the race: B's screen still shows the old version when B clicks.
  await b.routeWebSocket(/\/hub\/trains/, () => { /* swallow: no server connection, no messages */ })
  await signIn(a, 'alice@example.com')
  await signIn(b, 'bob@example.com')

  const t = await trainByTitle(a, 'Reporting')
  const gate = t.gates.find(g => g.name === 'Code Freeze')!
  await b.goto(`/trains/${t.id}/gates/${gate.id}`)
  await expect(b.locator('.inspector h2')).toHaveText('Code Freeze')
  await expect(b.locator('.inspector button:has-text("Start gate")')).toBeVisible()

  // Alice starts the gate first
  const started = await a.evaluate(async ([id, version]) => (await fetch(`/api/v1/gates/${id}:start`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': String(version) }, body: '{}' })).status, [gate.id, gate.version] as const)
  expect(started).toBe(200)

  await b.click('.inspector button:has-text("Start gate")')                 // Bob acts on the old version
  const alert = b.locator('.conflict')
  await expect(alert).toBeVisible()
  await expect(alert).toContainText('alice')                                  // who
  await expect(alert).toContainText('changed this gate before your change was saved')
  await expect(alert).toContainText('Your change was not applied')
  await expect(alert).toContainText('InProgress')                             // what it is now
  await expect(b.locator('[role=dialog], dialog')).toHaveCount(0)             // never a modal
  await expect(b.locator('.inspector button:has-text("Start gate")')).toHaveCount(0)   // and the screen caught up to the current state
})

test('a window edit that loses the race keeps the draft and offers overwrite or use theirs', async ({ browser }) => {
  const a = await (await browser.newContext()).newPage()
  const b = await (await browser.newContext()).newPage()
  await b.routeWebSocket(/\/hub\/trains/, () => {})
  await signIn(a, 'alice2@example.com')
  await signIn(b, 'bob2@example.com')
  const t = await trainByTitle(a, 'Payments platform')

  await b.goto(`/trains/${t.id}`)
  await b.click('button:has-text("Edit window")')
  await b.fill('label:has-text("Starts") input', '2026-12-01T20:00')
  await b.fill('label:has-text("Ends") input', '2026-12-01T23:00')

  // Alice saves a different window first (she has the current version)
  const alice = await a.evaluate(async id => {
    const w = await (await fetch(`/api/v1/trains/${id}/window`)).json() as { version: number }
    const r = await fetch(`/api/v1/trains/${id}/window`, { method: 'PUT', headers: { 'Content-Type': 'application/json', 'If-Match': String(w.version) }, body: JSON.stringify({ startsAt: '2026-12-02T01:00:00Z', endsAt: '2026-12-02T03:00:00Z' }) })
    return r.status
  }, t.id)
  expect(alice).toBe(200)

  await b.click('button:has-text("Save window")')
  const alert = b.locator('.conflict')
  await expect(alert).toContainText('changed this window before your change was saved')
  await expect(alert).toContainText('The window is now')
  await expect(b.locator('label:has-text("Starts") input')).toHaveValue('2026-12-01T20:00')      // Bob's draft is intact
  await expect(b.locator('label:has-text("Ends") input')).toHaveValue('2026-12-01T23:00')

  await b.click('button:has-text("Overwrite with mine")')                                          // Bob decides
  await expect(b.locator('.conflict')).toHaveCount(0)
  const now = await b.evaluate(async id => (await (await fetch(`/api/v1/trains/${id}/window`)).json() as { startsAt: string }).startsAt, t.id)
  expect(new Date(now).getUTCHours()).toBe(new Date('2026-12-01T20:00').getUTCHours())
})
