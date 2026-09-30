import AxeBuilder from '@axe-core/playwright'
import { createHash } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { expect, test } from '@playwright/test'
import { signIn, trainByTitle } from './support'

// REOS-50: PDF exports on the train view. The app must be started with Pdf__QuestPdfLicense=Community (or Professional/Enterprise): the licence is the deployer's choice
// (DECISIONS OI-2) and is never defaulted, so without it the panel must say so readably instead of queuing a job.
const licensed = !!process.env.Pdf__QuestPdfLicense

test('an RTE generates a release report, sees it finish and its SHA-256 matches the downloaded file', async ({ page }) => {
  await signIn(page, 'rte@example.com', 'RTE')
  const t = await trainByTitle(page, 'Reporting')
  await page.goto(`/trains/${t.id}`)

  await page.locator('.header-line button:text-is("Export")').click()      // scrolls to the Exports section
  const panel = page.locator('section[aria-label="Exports"]')
  await expect(panel).toBeInViewport()
  await expect(panel.locator('button:text-is("Generate evidence pack")')).toBeVisible()   // RTE has audit rights

  await panel.locator('button:text-is("Generate release report")').click()
  if (!licensed) {
    await expect(panel.locator('[role=alert]')).toContainText('Pdf:QuestPdfLicense')
    return
  }
  const row = panel.locator('tr', { hasText: 'Release report' }).first()
  await expect(row).toBeVisible()
  await expect(row).toContainText(/Queued|Running|Done/)
  await expect(row).toContainText('✓ Done', { timeout: 45_000 })          // the worker polls every few seconds; the panel refreshes itself

  const shaCell = row.locator('td[title]')
  const fullSha = await shaCell.getAttribute('title')
  expect(fullSha).toMatch(/^[0-9a-f]{64}$/)
  const [download] = await Promise.all([page.waitForEvent('download'), row.locator('a:text-is("Download")').click()])
  const path = await download.path()
  expect(createHash('sha256').update(await readFile(path)).digest('hex')).toBe(fullSha)
  expect(download.suggestedFilename()).toMatch(/^release-report-.*\.pdf$/)

  const results = await new AxeBuilder({ page }).include('section[aria-label="Exports"]').analyze()
  expect(results.violations.map(v => `${v.id}: ${v.nodes[0]?.html}`)).toEqual([])
})

test('a Governance Officer generates the evidence pack ZIP and its SHA-256 verifies', async ({ page }) => {
  test.skip(!licensed, 'needs Pdf__QuestPdfLicense')
  await signIn(page, 'gov@example.com', 'GovernanceOfficer')
  const t = await trainByTitle(page, 'Reporting')
  await page.goto(`/trains/${t.id}`)
  const panel = page.locator('section[aria-label="Exports"]')
  await panel.locator('button:text-is("Evidence pack with files (ZIP)")').click()
  const row = panel.locator('tr', { hasText: 'Audit evidence pack' }).first()
  await expect(row).toContainText('ZIP with files')
  await expect(row).toContainText(/✓ Done|✗ Failed/, { timeout: 60_000 })
  await expect(row).toContainText('✓ Done')
  const fullSha = await row.locator('td[title]').getAttribute('title')
  const [download] = await Promise.all([page.waitForEvent('download'), row.locator('a:text-is("Download")').click()])
  expect(createHash('sha256').update(await readFile(await download.path())).digest('hex')).toBe(fullSha)
  expect(download.suggestedFilename()).toMatch(/\.zip$/)
})

test('a Viewer can generate the ordinary documents but is not offered the evidence pack', async ({ page }) => {
  await signIn(page, 'viewer@example.com', 'Viewer')
  const t = await trainByTitle(page, 'Reporting')
  await page.goto(`/trains/${t.id}`)
  const panel = page.locator('section[aria-label="Exports"]')
  await expect(panel.locator('button:text-is("Generate release report")')).toBeVisible()
  await expect(panel.locator('button:text-is("Generate run sheet")')).toBeVisible()
  await expect(panel.locator('button:text-is("Generate scorecard")')).toBeVisible()
  await expect(panel.locator('button', { hasText: 'evidence pack' })).toHaveCount(0)
  await expect(panel.locator('button', { hasText: 'Evidence pack' })).toHaveCount(0)
  // the API refuses it as well
  const status = await page.evaluate(async id => (await fetch(`/api/v1/trains/${id}/export-jobs`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ kind: 'EvidencePack' }) })).status, t.id)
  expect(status).toBe(403)
})
