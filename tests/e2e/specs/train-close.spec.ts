import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { openTab, signIn } from './support'

// REOS-86 / REOS-127 (D53): the release header ends a release with "Complete…" (close code + notes) and "Abort…" (reason), as confirmation dialogs.
// REOS-87: a live run rolled back requires the train's post-implementation review, due within a business day. Each test makes its own drill train.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function drill(page: Page) {
  return page.evaluate(async () => (await fetch('/api/v1/dev/live-drill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()) as Promise<{ trainId: string; runId: string; steps: Record<string, string> }>
}

/** Fails the drill's running step R-002 and ends the live run with `outcome`, through the API as the signed-in RTE. */
async function endRun(page: Page, d: { runId: string; steps: Record<string, string> }, outcome: 'RolledBack' | 'Aborted') {
  await page.evaluate(async ([runId, stepId, outcome]) => {
    const json = { 'Content-Type': 'application/json' }
    const detail = async () => (await fetch(`/api/v1/runs/${runId}`)).json() as Promise<{ version: number; steps: { stepId: string; version: number }[] }>
    const step = (await detail()).steps.find(s => s.stepId === stepId)!
    const failed = await fetch(`/api/v1/runs/${runId}/steps/${stepId}:fail`, { method: 'POST', headers: { ...json, 'If-Match': String(step.version) }, body: JSON.stringify({ note: 'e2e' }) })
    if (!failed.ok) throw new Error(`fail step: ${failed.status} ${await failed.text()}`)
    const ended = await fetch(`/api/v1/runs/${runId}:end`, { method: 'POST', headers: { ...json, 'If-Match': String((await detail()).version) }, body: JSON.stringify({ outcome }) })
    if (!ended.ok) throw new Error(`end run: ${ended.status} ${await ended.text()}`)
  }, [d.runId, d.steps['R-002'], outcome] as const)
}

const pirSection = (page: Page) => page.locator('section[aria-label="Post-implementation review"]')

test('a rolled-back live run requires a review with a due date, and Abort asks for a reason first', async ({ page }) => {
  await signIn(page, 'close-abort@example.com')
  const d = await drill(page)
  await endRun(page, d, 'RolledBack')
  await page.goto(`/trains/${d.trainId}`)
  await openTab(page, 'Activity')                                                  // the review is on the Activity tab

  await expect(pirSection(page)).toContainText('a live run was rolled back')
  await expect(pirSection(page)).toContainText(/due \w{3},? \d{1,2} \w{3}/)

  const abort = page.getByRole('button', { name: 'Abort…' })
  await abort.click()
  const dialog = page.getByRole('dialog', { name: 'Abort this release?' })
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()       // the dialog starts on the safe choice
  await expect(dialog).toContainText('requires a post-implementation review')
  await page.keyboard.press('Escape')                                              // Escape cancels and focus returns to the opener
  await expect(dialog).toBeHidden()
  await expect(abort).toBeFocused()
  await expect(page).toHaveURL(new RegExp(`/trains/${d.trainId}$`))

  await abort.click()
  await dialog.getByRole('button', { name: 'Abort release' }).click()
  await expect(dialog.getByRole('alert')).toContainText('Reason')                  // a reason is required: refused inside the dialog, which stays open
  await dialog.getByLabel('Reason (required)').fill('Rolled back; not retrying in this window')
  await dialog.getByRole('button', { name: 'Abort release' }).click()

  await expect(dialog).toBeHidden()
  await expect(page.locator('.stream-row[aria-current=true]')).toContainText('Aborted')
  await expect(page.getByRole('button', { name: 'Abort…' })).toHaveCount(0)
  await expect(pirSection(page)).toContainText('a live run was rolled back')       // the rollback's review stands; aborting adds no second one
})

test('Complete asks for a close code; closing with issues requires a review', async ({ page }) => {
  await signIn(page, 'close-complete@example.com')
  const d = await drill(page)
  await endRun(page, d, 'Aborted')          // the run was stopped, not rolled back: no review yet
  await page.goto(`/trains/${d.trainId}`)
  await expect(page.locator('h1')).toContainText('Live drill')
  await openTab(page, 'Activity')
  await expect(pirSection(page)).toHaveCount(0)
  await page.getByRole('button', { name: 'Complete…' }).click()
  const dialog = page.getByRole('dialog', { name: 'Complete this release?' })
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await dialog.getByLabel('Close code').selectOption({ label: 'Successful with issues' })
  await expect(dialog).toContainText('A post-implementation review will be required.')
  await dialog.getByLabel('Notes (optional)').fill('Card authorisation latency above target for 20 minutes')
  const r = await new AxeBuilder({ page }).include('dialog[open]').withTags(TAGS).analyze()
  expect(r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical').map(v => v.id)).toEqual([])
  await dialog.getByRole('button', { name: 'Complete release' }).click()

  await expect(dialog).toBeHidden()
  await expect(page.locator('.stream-row[aria-current=true]')).toContainText('SuccessfulWithIssues')
  await expect(pirSection(page)).toContainText('close code SuccessfulWithIssues')
  await expect(page.getByRole('button', { name: 'Complete…' })).toHaveCount(0)
})
