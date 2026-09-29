import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-32: the live runbook screen. Countdowns run on the server's clock, so a wrong browser clock changes nothing.
const SKEW_MS = 7 * 60 * 1000

async function drill(page: Page) {
  return page.evaluate(async () => (await fetch('/api/v1/dev/live-drill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()) as Promise<{ trainId: string; runId: string; steps: Record<string, string> }>
}
const row = (page: Page, code: string) => page.locator('tr', { has: page.locator('td', { hasText: new RegExp(`^${code}$`) }) })
const seconds = (hms: string) => { const neg = hms.startsWith('−'); const [h, m, s] = hms.replace('−', '').split(':').map(Number); return (neg ? -1 : 1) * (h * 3600 + m * 60 + s) }

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

  await page.goto(`/trains/${d.trainId}/live`)
  const countdown = page.getByTestId('window-countdown')
  await expect(countdown).toHaveText(/\d:\d\d:\d\d/)
  await page.waitForTimeout(1200)                                                          // let a ServerTime offset arrive

  // The window closes 90 minutes after the drill was created; ask the server, not the browser
  const fc = await page.evaluate(async id => (await fetch(`/api/v1/runs/${id}/forecast`)).json() as Promise<{ windowClosesInSec: number }>, d.runId)
  const shown = seconds((await countdown.textContent())!)
  expect(Math.abs(shown - fc.windowClosesInSec), `countdown ${shown}s vs server ${fc.windowClosesInSec}s (a browser-clock countdown would be ~${SKEW_MS / 1000}s off)`).toBeLessThanOrEqual(5)

  // the screen: metrics, table with sections, the running step's timer, and the rollback section kept apart
  await expect(page.locator('section.metrics')).toContainText('Steps done')
  await expect(page.locator('section.metrics')).toContainText('1 / 5')
  await expect(page.locator('tr.sec', { hasText: 'Pre-check' })).toBeVisible()
  await expect(page.locator('tr.sec', { hasText: 'Rollback' })).toBeVisible()
  await expect(row(page, 'R-002')).toContainText('●')                // running: ● m:ss

  // the drawer for the running step; mark it done
  await page.locator('button', { hasText: 'Deploy Payments API' }).click()
  const drawer = page.locator('.inspector')
  await expect(drawer.locator('h2')).toHaveText('Deploy Payments API')
  await expect(drawer).toContainText('left at plan pace')
  await expect(drawer).toContainText('Promote the build to the green slot')                // instructions
  await drawer.locator('button:text-is("Mark done")').click()
  await expect(page.locator('section.metrics')).toContainText('2 / 5')
  await expect(row(page, 'R-002')).toContainText('✓ Done')
  await expect(row(page, 'R-003')).toContainText('Ready')             // its dependency is done

  // Skip needs a note
  await page.locator('button', { hasText: 'Deploy Card Portal' }).click()
  await drawer.locator('button:text-is("Skip…")').click()
  await drawer.locator('button:text-is("Confirm skip")').click()
  await expect(drawer.getByRole('alert')).toContainText('needs a note')
  await drawer.locator('#step-note').fill('Card Portal is out of scope for this drill')
  await drawer.locator('button:text-is("Confirm skip")').click()
  await expect(row(page, 'R-003')).toContainText('Skipped')
  await ctx.close()
})

for (const scheme of ['light', 'dark'] as const) {
  test(`live runbook has no serious axe violations (${scheme})`, async ({ browser }) => {
    const ctx = await browser.newContext({ colorScheme: scheme, viewport: { width: 1440, height: 900 } })
    const page = await ctx.newPage()
    await signIn(page, `live-a11y-${scheme}@example.com`)
    const d = await drill(page)
    await page.goto(`/trains/${d.trainId}/live/steps/${d.steps['R-002']}`)
    await expect(page.locator('.inspector h2')).toHaveText('Deploy Payments API')
    await expect(page.locator('.runtable')).toBeVisible()
    await page.screenshot({ path: `test-results/live-${scheme}.png` })
    const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()
    const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
    expect(bad, bad.map(v => `${v.id}: ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join(' | ')}`).join('\n')).toEqual([])
    await ctx.close()
  })
}
