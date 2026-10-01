import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { signIn, trainByTitle } from './support'

// REOS-81: add, edit and remove a product and a gate in the train workspace (inline forms, Inspector, inline confirms; no modal).
// Everything added here is removed again, so the shared demo database ends as it started (apart from versions and audit rows).
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}

test('a product and a gate are added, edited and removed from the workspace', async ({ page }) => {
  await signIn(page, 'plan81@example.com')
  const t = await trainByTitle(page, 'Reporting')
  await page.goto(`/trains/${t.id}`)

  // ---- product: add inline, edit in its row, remove with an inline confirm
  const products = page.locator('section[aria-label="Bundled products"]')
  await products.locator('.section-head button:text-is("Add product")').click()
  const form = products.getByRole('group', { name: 'New product' })
  await form.locator('label:has-text("Product") input').fill('Search Svc')
  await form.locator('label:has-text("Version") input').fill('1.0.0')
  await form.locator('label:has-text("Project") input').fill('SRC')
  await form.locator('button:text-is("Add product")').click()
  const row = products.locator('tr', { hasText: 'Search Svc' })
  await expect(row).toContainText('1.0.0')
  await expect(form).toHaveCount(0)

  await products.getByRole('button', { name: 'Edit Search Svc' }).click()
  await products.getByLabel('Version tag').fill('1.0.1')
  await products.locator('button:text-is("Save")').click()
  await expect(products.locator('tr', { hasText: 'Search Svc' })).toContainText('1.0.1')

  // A product that checklist tasks still point at is refused, and the refusal names what holds it.
  await products.getByRole('button', { name: 'Remove Payments API' }).click()
  await products.locator('button:text-is("Confirm remove")').click()
  await expect(products.getByRole('alert')).toContainText('Payments API is still used by')
  await expect(products.locator('tr', { hasText: 'Payments API' })).toHaveCount(1)

  await products.getByRole('button', { name: 'Remove Search Svc' }).click()
  await products.getByRole('group', { name: 'Confirm removing Search Svc' }).locator('button:text-is("Confirm remove")').click()
  await expect(products.locator('tr', { hasText: 'Search Svc' })).toHaveCount(0)

  // ---- gate: add under the timeline, edit in the Inspector, remove (Pending only) with an inline confirm
  const timeline = page.locator('section[aria-label="Gate timeline"]')
  await timeline.locator('.section-head button:text-is("Add gate")').click()
  const gateForm = timeline.getByRole('group', { name: 'New gate' })
  await gateForm.locator('label:has-text("Gate") input').fill('Docs review')
  await gateForm.getByLabel('Business days before the target').fill('2')
  await gateForm.locator('label:has-text("Owner") select').selectOption({ index: 1 })
  await gateForm.locator('button:text-is("Add gate")').click()
  const insp = page.locator('.inspector')
  await expect(insp.locator('h2')).toHaveText('Docs review')
  await expect(insp).toContainText('T−2')
  await expect(page.locator('svg.timeline')).toContainText('Docs review')

  await insp.locator('button:text-is("Edit gate")').click()
  const edit = insp.getByRole('group', { name: 'Edit gate' })
  await edit.locator('label:has-text("Gate") input').fill('Docs sign-off')
  await edit.getByLabel('Business days before the target').fill('3')
  await insp.locator('button:text-is("Save gate")').click()
  await expect(insp.locator('h2')).toHaveText('Docs sign-off')
  await expect(insp).toContainText('T−3')
  await expect(page.locator('svg.timeline')).toContainText('Docs sign-off')

  await insp.locator('button:text-is("Remove gate")').click()
  await expect(insp.getByRole('group', { name: 'Confirm removal' })).toContainText('Remove Docs sign-off?')
  await insp.locator('button:text-is("Confirm remove")').click()
  await expect(page.locator('svg.timeline')).not.toContainText('Docs sign-off')
  await expect(insp).toContainText('Select a train, gate or task')
})

test('a viewer sees no add, edit or remove actions', async ({ page }) => {
  await signIn(page, 'plan81-viewer@example.com', 'Viewer')
  const t = await trainByTitle(page, 'Reporting')
  await page.goto(`/trains/${t.id}/gates/${t.gates[0].id}`)
  await expect(page.locator('.inspector h2')).toHaveText(t.gates[0].name)
  await expect(page.locator('button:text-is("Add product")')).toHaveCount(0)
  await expect(page.locator('button:text-is("Add gate")')).toHaveCount(0)
  await expect(page.locator('button:text-is("Edit gate")')).toHaveCount(0)
  await expect(page.locator('button:text-is("Remove gate")')).toHaveCount(0)
})

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })
    test('the add and edit forms pass axe', async ({ page }) => {
      await signIn(page, `plan81-a11y-${scheme}@example.com`)
      const t = await trainByTitle(page, 'Reporting')
      const gate = t.gates.find(g => g.status === 'Pending')!
      await page.goto(`/trains/${t.id}/gates/${gate.id}`)
      await expect(page.locator('.inspector h2')).toHaveText(gate.name)
      await page.locator('section[aria-label="Bundled products"] .section-head button:text-is("Add product")').click()
      await page.locator('section[aria-label="Gate timeline"] .section-head button:text-is("Add gate")').click()
      await page.locator('section[aria-label="Bundled products"]').getByRole('button', { name: 'Edit Ledger Svc' }).click()
      await page.locator('.inspector button:text-is("Edit gate")').click()
      await page.locator('.inspector button:text-is("Remove gate")').waitFor({ state: 'detached' })
      await expect(page.locator('.inspector').getByRole('group', { name: 'Edit gate' })).toBeVisible()
      await scan(page, `plan structure forms (${scheme})`)
      // leave nothing open for the next spec (drafts live in the session)
      await page.locator('.inspector button:text-is("Cancel")').click()
      await page.locator('section[aria-label="Bundled products"] button:text-is("Cancel")').first().click()
      await page.locator('section[aria-label="Bundled products"] button:text-is("Cancel")').first().click()
      await page.locator('section[aria-label="Gate timeline"] button:text-is("Cancel")').click()
      await page.locator('.inspector button:text-is("Remove gate")').click()
      await expect(page.locator('.inspector').getByRole('group', { name: 'Confirm removal' })).toBeVisible()
      await scan(page, `gate removal confirm (${scheme})`)
      await page.locator('.inspector button:text-is("Keep gate")').click()
    })
  })
}
