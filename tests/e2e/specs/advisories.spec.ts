import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-88: the Runbook shows the plan's contingency before the rollback deadline. REOS-85: two open trains whose windows overlap and that share a CI
// warn on the window line and as advisory readiness items (never as blockers). Two live drills overlap by construction (both open now).

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now() % 1_000_000

async function drill(page: Page) {
  return page.evaluate(async () => (await fetch('/api/v1/dev/live-drill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()) as Promise<{ trainId: string }>
}
async function addCi(page: Page, trainId: string, ciName: string) {
  const status = await page.evaluate(async ([id, n]) => (await fetch(`/api/v1/trains/${id}/cis`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ciName: n }) })).status, [trainId, ciName] as const)
  expect(status).toBeLessThan(300)
}
const titleOf = (page: Page, id: string) => page.evaluate(async i => (await (await fetch(`/api/v1/trains/${i}`)).json() as { title: string }).title, id)

test('the runbook shows its contingency, and two trains sharing a CI in overlapping windows warn on both', async ({ page }) => {
  await signIn(page, 'advisories@example.com')
  const a = await drill(page), b = await drill(page)
  const ci = `e2e-ledger-db-${stamp}`
  await addCi(page, a.trainId, ci)
  await addCi(page, b.trainId, ci)
  const bTitle = await titleOf(page, b.trainId)

  await page.goto(`/trains/${a.trainId}`)
  // The drill plans 90 min of work in a 120 min window with a 25 min rollback: 5 min spare, well under the 30% minimum.
  const runbook = page.locator('section[aria-label=Runbook]')
  await expect(runbook.locator('p.contingency')).toContainText(/▲ Contingency \d+%, below the 30% minimum/)
  await expect(runbook.locator('p.contingency')).toContainText('rollback 25 m')

  const readiness = page.locator('section[aria-label=Readiness]')
  await expect(readiness).toContainText(`Window overlaps ${bTitle}`)
  await expect(readiness).toContainText(`CI ${ci}`)
  await expect(readiness).toContainText('(advisory)')
  await expect(page.locator('[aria-label="Release context"]')).toContainText(`overlaps ${bTitle}`)    // the window line under the title
  await expect(page.getByRole('button', { name: 'Complete…' })).toBeEnabled()   // advisories never block

  const r = await new AxeBuilder({ page }).include('section[aria-label=Readiness]').include('section[aria-label=Runbook]').withTags(TAGS).analyze()
  expect(r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical').map(v => v.id)).toEqual([])

  await page.goto(`/trains/${b.trainId}`)
  await expect(page.locator('section[aria-label=Readiness]')).toContainText(`CI ${ci}`)   // the other train warns too
})
