import { expect, test } from '@playwright/test'
import { signIn, trainByTitle } from './support'

// REOS-29: build a runbook in the UI; a dependency that would loop is refused with the loop named, and nothing changes.
test('runbook steps, dependencies and cycle refusal work from the UI', async ({ page }) => {
  await signIn(page, 'runbook@example.com')
  const t = await trainByTitle(page, 'Reporting')
  await page.goto(`/trains/${t.id}`)
  const rb = page.locator('section[aria-label=Runbook]')
  await expect(rb).toContainText('No runbook steps yet')

  // The Add step form stays open after each Add (Enter submits), so it is opened once for the three steps.
  await rb.locator('.section-head button:has-text("Add step")').click()
  async function addStep(title: string, section: string, start: string, minutes: string) {
    await rb.locator('label:has-text("Step") input').fill(title)
    await rb.locator('label:has-text("Section") select').selectOption(section)
    await rb.locator('label:has-text("Starts") input').fill(start)
    await rb.locator('label:has-text("Minutes") input').fill(minutes)
    await rb.locator('button:text-is("Add")').click()
    await expect(rb.locator('button', { hasText: title })).toBeVisible()
    await expect(rb.locator('label:has-text("Step") input')).toHaveValue('')                   // cleared and ready for the next step
  }
  await addStep('Stop traffic', 'PreCheck', '2026-11-06T21:00', '10')
  await addStep('Deploy API', 'Deploy', '2026-11-06T21:15', '30')
  await addStep('Restore snapshot', 'Rollback', '2026-11-06T22:00', '45')
  await expect(rb.locator('h3', { hasText: 'Rollback' })).toBeVisible()                      // the rollback section is supported
  await expect(rb.locator('tr', { hasText: 'Deploy API' }).locator('.mono').first()).toHaveText('R-002')

  // Deploy API depends on Stop traffic
  await rb.locator('button', { hasText: 'Deploy API' }).click()
  const insp = page.locator('.inspector')
  await expect(insp.locator('h2')).toHaveText('Deploy API')
  await insp.locator('label:has-text("Add dependency") select').selectOption({ label: 'R-001 Stop traffic' })
  await insp.locator('button:text-is("Add")').click()
  await expect(rb.locator('tr', { hasText: 'Deploy API' })).toContainText('R-001')

  // Stop traffic depending on Deploy API would loop: refused, loop named, table unchanged
  await rb.locator('button', { hasText: 'Stop traffic' }).click()
  await expect(insp.locator('h2')).toHaveText('Stop traffic')
  await insp.locator('label:has-text("Add dependency") select').selectOption({ label: 'R-002 Deploy API' })
  await insp.locator('button:text-is("Add")').click()
  const alert = insp.getByRole('alert')
  await expect(alert).toContainText('dependency cycle')
  await expect(alert).toContainText('R-001')
  await expect(alert).toContainText('R-002')
  await expect(rb.locator('tr', { hasText: 'Stop traffic' }).locator('td').last()).toHaveText('—')
})
