import AxeBuilder from '@axe-core/playwright'
import { expect, test, type APIRequestContext, type Page, type Playwright } from '@playwright/test'
import { signIn, signOut } from './support'

// REOS-98 and REOS-128: the Orchestration pipeline, with the Rehearsal and Live run views inside it. The scenario is the live drill (an Executing release with a Live run: R-001 done,
// R-002 running) plus a canary deployment pushed through the real ingest API, linked to that run by the release's key. Each test makes its own drill, so none depends on another.
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
interface Drill { trainId: string; runId: string; steps: Record<string, string> }
const drill = async (page: Page) => (await json(page, 'POST', '/api/v1/dev/live-drill', {})).body as Drill

/** A Running canary at 25 % (step 2 of 4) in a Production environment, linked to the drill's Live run through the release key, with 20 minutes of samples. */
async function canary(page: Page, playwright: Playwright, d: Drill): Promise<{ id: string; tool: APIRequestContext }> {
  const stamp = `${d.runId.slice(-6)}-${Date.now()}`
  const service = `chk-${stamp}`, env = `Prod EU ${stamp}`, externalId = `dep-${stamp}`
  expect([200, 201]).toContain((await json(page, 'POST', '/api/v1/services', { serviceKey: service, name: `Checkout ${stamp}` })).status)
  expect([200, 201]).toContain((await json(page, 'POST', '/api/v1/environments', { name: env, kind: 'Production', region: 'eu-west-1', errorRateMaxPct: 0.5, latencyP95MaxMs: 300, availabilityMinPct: 99.5, sloAvailabilityPct: 99.9, thresholdForSeconds: 120 })).status)
  const key = await json(page, 'POST', '/api/v1/ingest-keys', { name: `e2e ${stamp}` })
  const tool = await playwright.request.newContext({ baseURL: new URL(page.url()).origin, extraHTTPHeaders: { Authorization: `Bearer ${(key.body as { secret: string }).secret}` } })
  const releaseKey = ((await json(page, 'GET', `/api/v1/trains/${d.trainId}/release`)).body as { key: string }).key
  const now = Date.now(), at = (m: number) => new Date(now - m * 60000).toISOString().replace(/\.\d{3}Z$/, 'Z')
  const dep = await tool.post('/api/v1/ingest/deployments', { data: { externalId, serviceKey: service, environment: env, releaseVersion: 'v3.18.0', strategy: 'Canary', canarySteps: [5, 25, 50, 100], state: 'Running', trafficPercent: 25, readyPods: 24, desiredPods: 96, releaseKey, startedAt: at(20) } })
  expect(dep.status(), await dep.text()).toBe(201)
  const id = ((await dep.json()) as { id: string }).id
  const samples = Array.from({ length: 20 }, (_, i) => [
    { series: 'Canary', at: at(20 - i), errorRatePct: 0.08, latencyP95Ms: 184, availabilityPct: 99.96, requestCount: 1000 },
    { series: 'Stable', at: at(20 - i), errorRatePct: 0.05, latencyP95Ms: 196, availabilityPct: 99.97, requestCount: 9000 },
  ]).flat()
  expect((await tool.post('/api/v1/ingest/metrics', { data: { deploymentExternalId: externalId, samples } })).status()).toBe(200)
  return { id, tool }
}

/** A step event made by someone else (a second person on the call): it needs the step's current version as If-Match. */
async function stepEvent(page: Page, d: Drill, code: string, action: 'start' | 'done' | 'fail') {
  const run = (await json(page, 'GET', `/api/v1/runs/${d.runId}`)).body as { steps: { stepCode: string; version: number }[] }
  const version = run.steps.find(s => s.stepCode === code)!.version
  return page.evaluate(async ([r, s, a, v]) => (await fetch(`/api/v1/runs/${r}/steps/${s}:${a}`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': String(v) }, body: '{}' })).status,
    [d.runId, d.steps[code], action, version] as const)
}
const steps = (page: Page, stage: string) => page.getByRole('table', { name: `${stage} steps` })
const stepRow = (page: Page, stage: string, code: string) => steps(page, stage).getByRole('row').filter({ hasText: new RegExp(`${code} · `) })

test('the pipeline shows the run: header, run context, stage cards, steps, canary; selecting a stage updates both panels; changes arrive live', async ({ page, playwright }) => {
  await signIn(page, 'orch-pipeline@example.com')
  const d = await drill(page)
  await canary(page, playwright, d)
  await page.goto(`/orchestration/${d.trainId}/live`)

  await expect(page.getByRole('heading', { level: 1, name: 'Orchestration pipeline' })).toBeVisible()
  const trail = page.getByRole('navigation', { name: 'Breadcrumb' })
  await expect(trail).toContainText('Orchestration')
  await expect(trail).toContainText(/RLS-\d+/)
  await expect(page.getByRole('button', { name: 'Pause run' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'View deployment' })).toBeEnabled()
  const context = page.locator('.or-context')
  await expect(context).toContainText('Running')
  await expect(context).toContainText(/RUN-[0-9A-Z]{6}/)
  await expect(context).toContainText(/Started by .* at \d+ \w+ \d{4}, \d\d:\d\d UTC/)
  await expect(context).toContainText(/Elapsed \d+m \d\d s|Elapsed \d+m \d\ds/)
  await expect(page.getByRole('tab', { name: /^Pipeline/ })).toHaveAttribute('aria-selected', 'true')
  for (const name of ['Run history', 'Configuration', 'Variables & secrets']) await expect(page.getByRole('tab', { name: new RegExp(`^${name}`) })).toBeVisible()

  // four stages here (the drill's runbook has four sections): the layout copes with fewer than five
  const stages = page.getByRole('list', { name: 'Stages' })
  await expect(stages.getByRole('listitem')).toHaveCount(4)
  const pre = stages.getByRole('button', { name: /^Pre-check, Successful/ }), deploy = stages.getByRole('button', { name: /^Deploy, Running/ }), verify = stages.getByRole('button', { name: /^Verify, Planned/ })
  await expect(deploy).toHaveAttribute('aria-pressed', 'true')                       // the stage that is running is selected first
  await expect(stages.getByRole('button', { name: /^Rollback, Planned/ })).toContainText('Only if called')
  await expect(pre).toContainText('1 / 1 steps done')
  await expect(page.getByText('On failure: stop downstream stages')).toBeVisible()

  // the steps of the selected stage, with the Live chip and the last-event line
  await expect(steps(page, 'Deploy')).toBeVisible()
  await expect(stepRow(page, 'Deploy', 'R-002')).toContainText('Running')
  await expect(stepRow(page, 'Deploy', 'R-003')).toContainText('Queued')
  await expect(page.getByRole('region', { name: /Deploy · execution steps/ })).toContainText('Live')
  await expect(page.getByText(/Last event \d\d:\d\d:\d\d UTC · R-002 started/)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Open run logs →' })).toBeVisible()

  // the canary panel for the Deploy stage
  const panel = page.getByRole('region', { name: 'Canary analysis' })
  await expect(panel).toContainText('25%')
  await expect(panel).toContainText('Step 2 of 4')
  await expect(panel.getByRole('progressbar', { name: 'Canary traffic' })).toHaveAttribute('aria-valuenow', '25')
  await expect(panel).toContainText('Observation window')
  await expect(panel).toContainText(/0\.08% < 0\.5% threshold/)
  await expect(panel).toContainText(/184 ms < 300 ms threshold/)
  await expect(panel).toContainText('Automatic rollback')

  // selecting another stage updates the steps table and the canary panel
  await verify.click()
  await expect(verify).toHaveAttribute('aria-pressed', 'true')
  await expect(deploy).toHaveAttribute('aria-pressed', 'false')
  await expect(steps(page, 'Verify')).toBeVisible()
  await expect(stepRow(page, 'Verify', 'R-004')).toContainText('Queued')
  await expect(page.getByRole('region', { name: 'Canary analysis' })).toContainText('A canary rolls out in the Deploy stage')
  await pre.click()
  await expect(stepRow(page, 'Pre-check', 'R-001')).toContainText('Successful')

  // a change made elsewhere arrives without a reload
  await deploy.click()
  expect(await stepEvent(page, d, 'R-002', 'done')).toBe(200)
  await expect(stepRow(page, 'Deploy', 'R-002')).toContainText('Successful')
  await expect(deploy).toContainText('1 done · 1 queued')
  await expect(page.getByRole('region', { name: 'Run clock' })).toContainText('2 / 5')
  await expect(page.getByRole('region', { name: 'Run clock' })).toContainText('Window closes in')
})

test('plan, actual and forecast bars and the deadline alert: the words say what the bars show', async ({ page }) => {
  await signIn(page, 'orch-bars@example.com')
  const d = await drill(page)
  await page.goto(`/orchestration/${d.trainId}/live`)
  const bars = page.getByRole('table', { name: 'Plan, actual and forecast by step' })
  await expect(bars).toBeVisible()
  await expect(bars.getByRole('row').filter({ hasText: 'R-002' })).toContainText(/\d\d:\d\d– \(forecast end \d\d:\d\d\)/)   // running: actual so far and forecast end, as text
  await expect(bars.getByRole('row').filter({ hasText: 'R-003' })).toContainText(/Forecast \d\d:\d\d–\d\d:\d\d/)
  await expect(bars.locator('svg.or-bars')).not.toHaveCount(0)
  await expect(page.getByText('Rollback deadline').first()).toBeVisible()
  await expect(page.getByRole('region', { name: 'Run clock' })).toContainText('To rollback deadline')
  // a failure stops the forecast, and the page says so as a notice
  expect(await stepEvent(page, d, 'R-002', 'fail')).toBe(200)
  await expect(page.getByText(/The forecast is stopped: R-002 failed/)).toBeVisible()
  await expect(page.getByRole('button', { name: /^Deploy, Failed/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /^Verify, Blocked/ })).toBeVisible()      // the stages after the failure are held, in words
  await expect(page.getByRole('button', { name: /^Verify, Blocked/ })).toContainText('R-002 failed')
})

test('Pause run asks first, starts on Cancel, needs a reason, and the page shows the paused state; Resume brings it back', async ({ page }) => {
  await signIn(page, 'orch-pause@example.com')
  const d = await drill(page)
  await page.goto(`/orchestration/${d.trainId}/live`)
  const pause = page.getByRole('button', { name: 'Pause run' })
  await pause.click()
  const dialog = page.getByRole('dialog', { name: 'Pause this run?' })
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
  await expect(pause).toBeFocused()                                      // focus returns to what opened it
  await expect(page.locator('.or-context')).toContainText('Running')

  await pause.click()
  await dialog.getByRole('button', { name: 'Pause run' }).click()        // no reason yet
  await expect(dialog).toContainText('Say why the run is paused')
  await dialog.getByLabel('Reason (required)').fill('Waiting on SRE sign-off')
  await dialog.getByRole('button', { name: 'Pause run' }).click()
  await expect(dialog).toBeHidden()
  await expect(page.locator('.or-context')).toContainText('Paused')
  await expect(page.getByText(/Run paused by .*/)).toBeVisible()
  await expect(page.getByText(/Waiting on SRE sign-off/).first()).toBeVisible()
  await expect(page.getByRole('button', { name: 'Resume run' })).toBeVisible()
  await expect(page.getByRole('button', { name: /^Deploy, Running/ })).toBeVisible()      // steps already running keep running

  await page.getByRole('button', { name: 'Resume run' }).click()
  await expect(page.locator('.or-context')).toContainText('Running')
  await expect(page.getByRole('button', { name: 'Pause run' })).toBeVisible()
})

test('ending a run is a confirmation dialog; the plan is untouched; the ended run stays in the history', async ({ page }) => {
  await signIn(page, 'orch-end@example.com')
  const d = await drill(page)
  const planBefore = (await json(page, 'GET', `/api/v1/trains/${d.trainId}/steps`)).body
  await page.goto(`/orchestration/${d.trainId}/live`)
  await page.getByRole('button', { name: 'Abort run' }).click()
  const dialog = page.getByRole('dialog', { name: 'Abort this run?' })
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(page.locator('.or-context')).toContainText('Running')
  // the server will not end a run while a step is running: the dialog closes and the page says why, in words
  await page.getByRole('button', { name: 'Abort run' }).click()
  await dialog.getByRole('button', { name: 'Abort run' }).click()
  await expect(page.getByText('Finish or fail the running steps first: R-002')).toBeVisible()
  await expect(page.locator('.or-context')).toContainText('Running')
  expect(await stepEvent(page, d, 'R-002', 'fail')).toBe(200)
  await page.getByRole('button', { name: 'Abort run' }).click()
  await dialog.getByRole('button', { name: 'Abort run' }).click()
  await expect(page.locator('.or-context')).toContainText('Aborted')
  await expect(page.getByRole('button', { name: 'Abort run' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Pause run' })).toHaveCount(0)

  await page.getByRole('tab', { name: /^Run history/ }).click()
  const history = page.getByRole('table', { name: 'Runs of this release' })
  await expect(history.getByRole('row').filter({ hasText: 'Live' })).toContainText('Aborted')
  expect((await json(page, 'GET', `/api/v1/trains/${d.trainId}/steps`)).body).toEqual(planBefore)   // actuals never overwrite the plan
})

test('a rehearsal starts from Orchestration, a release switches into it, and the mode follows the release', async ({ page }) => {
  await signIn(page, 'orch-rehearsal@example.com')
  const d = await drill(page)
  await page.goto(`/orchestration/${d.trainId}/rehearsal`)
  await expect(page.getByText('There is no rehearsal run for this release yet')).toBeVisible()
  await page.getByRole('button', { name: 'Start rehearsal run' }).click()
  await expect(page.locator('.or-context')).toContainText('Running')
  await expect(page.getByRole('button', { name: /^Pre-check, Planned/ })).toBeVisible()
  await expect(page.getByRole('combobox', { name: 'Run mode' })).toHaveValue('rehearsal')
  await page.getByRole('combobox', { name: 'Run mode' }).selectOption('live')
  await expect(page).toHaveURL(new RegExp(`/orchestration/${d.trainId}/live$`))
  await expect(page.getByRole('button', { name: /^Deploy, Running/ })).toBeVisible()

  // from the release page, Rehearsal and Live runbook open here
  await page.goto(`/trains/${d.trainId}`)
  await page.getByRole('button', { name: 'Rehearsal', exact: true }).click()
  await expect(page).toHaveURL(new RegExp(`/orchestration/${d.trainId}/rehearsal$`))
  await expect(page.getByRole('heading', { level: 1, name: 'Orchestration pipeline' })).toBeVisible()
})

test('keyboard: stage cards, steps and the drawer work without a mouse; Escape closes the drawer and gives focus back', async ({ page }) => {
  await signIn(page, 'orch-keys@example.com')
  const d = await drill(page)
  await page.goto(`/orchestration/${d.trainId}/live`)
  const verify = page.getByRole('button', { name: /^Verify, Planned/ })
  await verify.focus()
  await page.keyboard.press('Enter')
  await expect(verify).toHaveAttribute('aria-pressed', 'true')
  await expect(steps(page, 'Verify')).toBeVisible()
  await page.getByRole('button', { name: /^Deploy, Running/ }).focus()
  await page.keyboard.press('Space')
  const r2 = stepRow(page, 'Deploy', 'R-002')
  await r2.focus()
  await page.keyboard.press('ArrowDown')
  await expect(stepRow(page, 'Deploy', 'R-003')).toBeFocused()
  await page.keyboard.press('ArrowUp')
  await page.keyboard.press('Enter')
  const drawer = page.getByRole('complementary', { name: 'Step R-002' })
  await expect(drawer).toBeVisible()
  await expect(page).toHaveURL(new RegExp(`/orchestration/${d.trainId}/live/steps/${d.steps['R-002']}$`))
  await page.keyboard.press('Escape')
  await expect(drawer).toBeHidden()
  await expect(r2).toBeFocused()
  // tabs: arrows move and select
  await page.getByRole('tab', { name: /^Pipeline/ }).focus()
  await page.keyboard.press('ArrowRight')
  await expect(page.getByRole('tab', { name: /^Run history/ })).toHaveAttribute('aria-selected', 'true')
  await page.keyboard.press('End')
  await expect(page.getByRole('tab', { name: /^Variables/ })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByText('No variables or secrets are kept here')).toBeVisible()
})

test('a Viewer follows the run but cannot start, pause, end or act on a step', async ({ page }) => {
  await signIn(page, 'orch-rte-setup@example.com')
  const d = await drill(page)
  await signOut(page)
  await signIn(page, 'orch-viewer@example.com', 'Viewer')
  await page.goto(`/orchestration/${d.trainId}/live`)
  await expect(page.getByRole('heading', { level: 1, name: 'Orchestration pipeline' })).toBeVisible()
  await expect(page.getByText('You can follow this run but not start, pause or end it.')).toBeVisible()
  const pause = page.getByRole('button', { name: 'Pause run' })
  await expect(pause).toBeDisabled()
  await expect(page.getByText('Only an RTE or Release Manager can pause a run.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Abort run' })).toHaveCount(0)
  await expect(stepRow(page, 'Deploy', 'R-002')).toContainText('Running')
  await expect(page.getByRole('button', { name: /^Mark done/ })).toHaveCount(0)
  await stepRow(page, 'Deploy', 'R-002').click()
  const drawer = page.getByRole('complementary', { name: 'Step R-002' })
  await expect(drawer).toContainText('Deploy Payments API')
  await expect(drawer.getByRole('button', { name: 'Mark done' })).toHaveCount(0)
  await expect(drawer.getByRole('button', { name: /Fail/ })).toHaveCount(0)
  // no run in the other mode: the Start button is there but says why it cannot be used
  await page.goto(`/orchestration/${d.trainId}/rehearsal`)
  await expect(page.getByRole('button', { name: 'Start rehearsal run' })).toBeDisabled()
  await expect(page.getByText('Only an RTE or Release Manager can start a run.')).toBeVisible()
})

test('more stages than fit on a row wrap instead of scrolling the page', async ({ page }) => {
  await signIn(page, 'orch-narrow@example.com')
  const d = await drill(page)
  await page.setViewportSize({ width: 820, height: 900 })
  await page.goto(`/orchestration/${d.trainId}/live`)
  await expect(page.getByRole('list', { name: 'Stages' })).toBeVisible()
  const fits = await page.evaluate(() => { const ol = document.querySelector('ol.or-stages') as HTMLElement; return ol.scrollWidth <= ol.clientWidth + 1 })
  expect(fits).toBe(true)
})

for (const scheme of ['light', 'dark'] as const) {
  test(`orchestration has no serious axe violations in ${scheme}: pipeline, drawer, history, configuration, no-run state`, async ({ browser, playwright }) => {
    const ctx = await browser.newContext({ colorScheme: scheme, viewport: { width: 1440, height: 1000 } })
    const page = await ctx.newPage()
    await signIn(page, `orch-a11y-${scheme}@example.com`)
    const d = await drill(page)
    await canary(page, playwright, d)
    await page.goto(`/orchestration/${d.trainId}/live`)
    await expect(page.getByRole('region', { name: 'Canary analysis' })).toContainText('Step 2 of 4')
    await scan(page, `pipeline (${scheme})`)
    await page.screenshot({ path: `test-results/orchestration-${scheme}.png`, fullPage: true })
    await stepRow(page, 'Deploy', 'R-002').click()
    await expect(page.getByRole('complementary', { name: 'Step R-002' })).toBeVisible()
    await scan(page, `step drawer (${scheme})`)
    await page.getByRole('button', { name: 'Pause run' }).click()
    await expect(page.getByRole('dialog', { name: 'Pause this run?' })).toBeVisible()
    await scan(page, `pause dialog (${scheme})`)
    await page.keyboard.press('Escape')
    for (const tab of ['Run history', 'Configuration', 'Variables & secrets']) {
      await page.getByRole('tab', { name: new RegExp(`^${tab}`) }).click()
      await scan(page, `${tab} (${scheme})`)
    }
    await page.goto(`/orchestration/${d.trainId}/rehearsal`)
    await expect(page.getByText('There is no rehearsal run for this release yet')).toBeVisible()
    await scan(page, `no run (${scheme})`)
    await ctx.close()
  })
}
