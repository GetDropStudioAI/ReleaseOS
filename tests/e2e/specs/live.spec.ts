import { expect, test, type Locator, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-32, REOS-128: the Live run view, now inside Orchestration. Countdowns run on the server's clock, so a wrong browser clock changes nothing.
const SKEW_MS = 7 * 60 * 1000

async function drill(page: Page) {
  return page.evaluate(async () => (await fetch('/api/v1/dev/live-drill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()) as Promise<{ trainId: string; runId: string; steps: Record<string, string> }>
}
const seconds = (hms: string) => { const neg = hms.startsWith('−'); const [h, m, s] = hms.replace('−', '').split(':').map(Number); return (neg ? -1 : 1) * (h * 3600 + m * 60 + s) }
const row = (table: Locator, code: string) => table.getByRole('row').filter({ hasText: new RegExp(`${code} · `) })
test('countdowns are right with the browser clock 7 minutes fast; steps can be finished from the drawer', async ({ browser }) => {
  const ctx = await browser.newContext()
  await ctx.addInitScript(skew => {
    const RealDate = Date
    class SkewedDate extends RealDate {
      constructor(...a: unknown[]) { if (a.length === 0) super(RealDate.now() + skew); else super(...(a as [number])) }
      static now() { return RealDate.now() + skew }
    }
    // @ts-expect-error replacing the global on purpose: this is the fault being injected
    window.Date = SkewedDate
  }, SKEW_MS)
  const page = await ctx.newPage()
  await signIn(page, 'live@example.com')
  const d = await drill(page)

  await page.goto(`/trains/${d.trainId}/live`)   // an old link: it opens the same run inside Orchestration
  await expect(page).toHaveURL(new RegExp(`/orchestration/${d.trainId}/live$`))
  const countdown = page.getByTestId('window-countdown')
  await expect(countdown).toHaveText(/\d:\d\d:\d\d/)
  await page.waitForTimeout(1200)                                                          // let a ServerTime offset arrive

  // The window closes 90 minutes after the drill was created; ask the server, not the browser
  const fc = await page.evaluate(async id => (await fetch(`/api/v1/runs/${id}/forecast`)).json() as Promise<{ windowClosesInSec: number }>, d.runId)
  const shown = seconds((await countdown.textContent())!)
  expect(Math.abs(shown - fc.windowClosesInSec), `countdown ${shown}s vs server ${fc.windowClosesInSec}s (a browser-clock countdown would be ~${SKEW_MS / 1000}s off)`).toBeLessThanOrEqual(5)

  // the screen: the clock, the stages, the running step's timer, and the rollback stage kept apart
  const clock = page.getByRole('region', { name: 'Run clock' })
  await expect(clock).toContainText('Steps done')
  await expect(clock).toContainText('1 / 5')
  await expect(page.getByRole('button', { name: /^Pre-check, Successful/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /^Rollback, Planned/ })).toBeVisible()
  const steps = page.getByRole('table', { name: 'Deploy steps' })
  await expect(row(steps, 'R-002')).toContainText('Running')

  // the drawer for the running step; mark it done
  await row(steps, 'R-002').click()
  const drawer = page.getByRole('complementary', { name: 'Step R-002' })
  await expect(drawer).toContainText('Deploy Payments API')
  await expect(drawer).toContainText('left at plan pace')
  await expect(drawer).toContainText('Promote the build to the green slot')                // instructions
  await drawer.getByRole('button', { name: 'Mark done', exact: true }).click()
  await expect(clock).toContainText('2 / 5')
  await expect(row(steps, 'R-002')).toContainText('Successful')
  await expect(row(steps, 'R-003')).toContainText('Queued')             // its dependency is done, so it can start
  await expect(row(steps, 'R-003').getByRole('button', { name: 'Start R-003' })).toBeVisible()

  // Skip needs a note
  await row(steps, 'R-003').click()
  const next = page.getByRole('complementary', { name: 'Step R-003' })
  await next.getByRole('button', { name: 'Skip…' }).click()
  await next.getByRole('button', { name: 'Confirm skip' }).click()
  await expect(next).toContainText('needs a note')
  await next.getByLabel('Reason for skipping (required)').fill('Card Portal is out of scope for this drill')
  await next.getByRole('button', { name: 'Confirm skip' }).click()
  await expect(row(steps, 'R-003')).toContainText('Skipped')
  await ctx.close()
})
