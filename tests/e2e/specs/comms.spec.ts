import { expect, test, type Page } from '@playwright/test'
import { signIn, trainByTitle } from './support'

// REOS-45: the Communicate drawer (mockups/Comms.html). The demo data has no communication templates and the hydrator is REOS-44's, so the
// drawer's three data calls are answered here; what is under test is the drawer itself: layout, token highlighting, guarded actions, the
// If-Match it sends, the clipboard and the Esc key. The API behaviour (bytes, immutability, allowlist) is covered by CommDispatchTests.

const ctx = (trainVersion: number) => ({
  asOf: '2026-10-29T20:12:07Z', trainVersion, trainTitle: 'R26.24 Q4 Payments Consolidated', targetReleaseDate: '2026-10-30',
  templates: [{ id: 'c1', templateType: 'Go/No-Go outcome', audience: 'All stakeholders', subjectLine: '[{ReleaseTitle}] Go/No-Go: {GoNoGoDecision}', markdownBody: '**{ReleaseTitle}** is {GoNoGoDecision}.\nConditions\n{Conditions}', version: 1 }],
  schedule: [
    { id: 's0', commTemplateId: 'c1', templateType: 'Readiness notice', audience: 'all', subjectLine: 'x', dueAt: '2026-10-23T14:00:00Z', sentAt: '2026-10-23T13:51:00Z', dispatchId: 'd0', state: 'sent', version: 2 },
    { id: 's1', commTemplateId: 'c1', templateType: 'Go/No-Go outcome', audience: 'all', subjectLine: 'x', dueAt: '2026-10-29T22:00:00Z', sentAt: null, dispatchId: null, state: 'ready', version: 1 },
    { id: 's2', commTemplateId: 'c1', templateType: 'Checkpoint', audience: 'ops, support', subjectLine: 'x', dueAt: '2026-10-27T14:00:00Z', sentAt: '2026-10-27T15:40:00Z', dispatchId: 'd2', state: 'sentLate', version: 2 },
  ],
  webhookTargets: [{ id: 'w1', name: 'release-ops', kind: 'Teams', host: 'hooks.example.test' }],
})

const preview = (trainVersion: number, tokenErrors: string[] = []) => ({
  text: '**R26.24 Q4 Payments Consolidated** is Go with conditions.\nConditions\n- Card Portal pen-test exceptions signed by CISO', tokenErrors, asOf: '2026-10-29T20:12:00Z', trainVersion,
})

async function mock(page: Page, opts: { version: number; tokenErrors?: string[]; dispatched?: { headers: Record<string, string>; body: unknown }[] }) {
  await page.route('**/api/v1/trains/*/comms/dispatch-context', r => r.fulfill({ json: ctx(opts.version) }))
  await page.route('**/api/v1/trains/*/comms:preview', r => r.fulfill({ json: preview(opts.version, opts.tokenErrors) }))
  await page.route('**/api/v1/trains/*/comms/dispatches**', r => r.fulfill({ json: { items: [], nextCursor: null, limit: 25 } }))
  await page.route('**/api/v1/trains/*/comms:dispatch', async r => {
    opts.dispatched?.push({ headers: r.request().headers(), body: r.request().postDataJSON() })
    const b = r.request().postDataJSON() as { channel: string; format?: string }
    await r.fulfill({ json: {
      id: 'd9', trainId: 't', templateId: 'c1', channel: b.channel, format: b.format ?? null, webhookName: b.channel === 'Webhook' ? 'release-ops' : null, subject: '[R26.24] Go/No-Go: Go with conditions',
      body: b.format === 'Markdown' ? '**R26.24** is Go' : '<b>R26.24</b> is Go', bodySha256: 'ab'.repeat(32), dispatchedByName: 'dev', dispatchedAt: '2026-10-29T20:13:00Z', isRehearsal: false,
      outcome: b.channel === 'Webhook' ? 'Delivered' : 'Handed', failureReason: null, scheduleItemId: 's1', dueAt: '2026-10-29T22:00:00Z', sentAt: '2026-10-29T20:13:00Z', late: false,
    } })
  })
}

async function open(page: Page, email: string, role = 'RTE') {
  await signIn(page, email, role)
  const t = await trainByTitle(page, 'Payments platform')
  await page.goto(`/trains/${t.id}`)
  await page.locator('button:text-is("Communicate")').click()
  return page.locator('.inspector .comms-drawer')
}

test('the Communicate button opens the drawer with the template, a hydrated preview, the schedule and Esc closes it', async ({ page }) => {
  await mock(page, { version: 7 })
  const drawer = await open(page, 'comms1@example.com')
  await expect(drawer.locator('h2')).toHaveText('Go/No-Go outcome')
  await expect(drawer.locator('.tabs button')).toHaveText(['Message', 'Schedule', 'Sent log'])

  await expect(drawer.locator('.src .tk').first()).toHaveText('{ReleaseTitle}')                         // token highlighting
  await expect(drawer).toContainText('3 tokens · all known ✓')
  await expect(drawer.locator('[aria-label="Hydrated preview"] strong')).toHaveText('R26.24 Q4 Payments Consolidated')
  await expect(drawer).toContainText('data as of')
  await expect(drawer).toContainText('v7')

  const schedule = drawer.locator('table.comms-schedule')
  await expect(schedule.locator('tr', { hasText: 'Checkpoint' })).toContainText('✓ sent')
  await expect(schedule.locator('tr', { hasText: 'Checkpoint' })).toContainText('late')                  // sent after it was due: said in words
  await expect(schedule.locator('tr', { hasText: 'Readiness notice' })).not.toContainText('late')
  await expect(schedule.locator('tr', { hasText: 'Go/No-Go outcome' })).toContainText('ready to send')
  for (const h of await schedule.locator('th').all()) await expect(h).not.toBeEmpty()                     // no empty headers

  // Esc typed in a field belongs to the field; elsewhere it closes the drawer and focus goes back to the Communicate button
  await expect(drawer.locator('h2')).toBeFocused()                                                           // focus moved into the drawer on open
  await drawer.locator('label:has-text("Template") select').focus()
  await page.keyboard.press('Escape')
  await expect(drawer).toBeVisible()
  await drawer.locator('h2').focus()
  await page.keyboard.press('Escape')
  await expect(page.locator('.comms-drawer')).toHaveCount(0)
  await expect(page.locator('button:text-is("Communicate")')).toBeFocused()
})

test('sending asks for the previewed train version, stores through the API and reports it; the webhook is chosen from approved destinations', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write'])
  const sent: { headers: Record<string, string>; body: unknown }[] = []
  await mock(page, { version: 7, dispatched: sent })
  const drawer = await open(page, 'comms2@example.com')
  await expect(drawer.locator('table.comms-schedule tr.selected')).toContainText('ready to send')          // the next unsent item is pre-selected with its template
  await drawer.locator('table.comms-schedule button:has-text("Go/No-Go outcome")').click()             // picking it explicitly keeps it
  await expect(drawer.locator('table.comms-schedule tr.selected')).toContainText('ready to send')

  await drawer.locator('button:text-is("Copy Markdown")').click()
  await expect(drawer.getByTestId('comms-result')).toContainText('Markdown copied and recorded')
  await expect(page.getByTestId('announcer')).toContainText('Markdown copied and recorded')                                  // spoken through the app's one live region
  expect(sent[0].headers['if-match']).toBe('7')                                                            // the version the preview showed
  expect(sent[0].body).toMatchObject({ channel: 'Copy', format: 'Markdown', templateId: 'c1', scheduleItemId: 's1' })
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe('**R26.24** is Go')              // the stored text is what was copied

  await drawer.locator('button:text-is("Copy rich text")').click()
  await expect(drawer.getByTestId('comms-result')).toContainText('Rich text copied and recorded')
  expect(sent[1].body).toMatchObject({ channel: 'Copy', format: 'RichText' })

  await expect(drawer.locator('select#comms-hook option')).toHaveText(['release-ops (hooks.example.test)'])
  await drawer.locator('button:text-is("Post to release-ops")').click()
  await expect(drawer.getByTestId('comms-result')).toContainText('Webhook release-ops: delivered')
  expect(sent[2].body).toMatchObject({ channel: 'Webhook', webhookDestinationId: 'w1' })
})

test('an unknown token disables every send and says why', async ({ page }) => {
  await mock(page, { version: 7, tokenErrors: ['Unknown token {Conditons}'] })
  const drawer = await open(page, 'comms3@example.com')
  await expect(drawer).toContainText('1 unknown token')
  for (const b of ['Copy rich text', 'Copy Markdown', 'Open mailto']) await expect(drawer.locator(`button:text-is("${b}")`)).toBeDisabled()
  await expect(drawer.locator('button:text-is("Post to release-ops")')).toBeDisabled()
  await expect(drawer).toContainText('Unknown tokens block sending: Unknown token {Conditons}')
})

test('a Viewer can read the drawer but cannot send, and is told why', async ({ page }) => {
  await mock(page, { version: 7 })
  const drawer = await open(page, 'comms4@example.com', 'Viewer')
  await expect(drawer.locator('button:text-is("Copy rich text")')).toBeDisabled()
  await expect(drawer).toContainText('Only an RTE or Release Manager sends communications.')
  await drawer.locator('.tabs button:text-is("Sent log")').click()
  await expect(drawer).toContainText('Nothing has been sent for this train yet')
})
