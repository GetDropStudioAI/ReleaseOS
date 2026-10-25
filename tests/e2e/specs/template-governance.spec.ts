import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Browser, type Page } from '@playwright/test'
import { signIn } from './support'

// REOS-122 (RT7): template governance. A template carries checklist tasks and milestones (days from the target); the approver is not the author and a Governance Officer approves a change to a
// Compliance gate; an approved template gets a new version whose approval retires the old one; a release made from a template gets its tasks and milestones, its milestones move with the target,
// and its Compliance gates are locked. Adds its own templates (unique names) and releases (2031 dates) to the shared demo database. axe in both themes.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']
const stamp = Date.now() % 1_000_000

async function scan(page: Page, where: string) {
  const r = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const bad = r.violations.filter(v => v.impact === 'serious' || v.impact === 'critical')
  const detail = bad.map(v => `${v.id} (${v.impact}): ${v.help}\n   ${v.nodes.slice(0, 3).map(n => n.target.join(' ')).join('\n   ')}`).join('\n')
  expect(bad, `axe violations on ${where}:\n${detail}`).toEqual([])
}

const openTemplates = async (page: Page) => {
  await page.click('nav[aria-label=Primary] a:has-text("Templates")')
  await expect(page.locator('main h1')).toHaveText('Train templates')
}
const templateRow = (page: Page, name: string, version?: number) =>
  page.locator('table.os-table tr', { hasText: name }).filter(version === undefined ? {} : { has: page.locator('td', { hasText: new RegExp(`^${version}$`) }) })

/** A second signed-in person in a fresh browser context, so one test can act as several roles at once. */
async function person(browser: Browser, scheme: 'light' | 'dark', email: string, role: string) {
  const ctx = await browser.newContext({ colorScheme: scheme })
  const page = await ctx.newPage()
  await signIn(page, email, role)
  return { page, close: () => ctx.close() }
}

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme })

    test('tasks and milestones in a template, approved by a Governance Officer, then a new version approved by someone else retires the old one', async ({ page, browser }) => {
      const name = `E2E governed ${scheme} ${stamp}`
      await signIn(page, `tg-rm-${scheme}@example.com`, 'ReleaseManager')
      await openTemplates(page)

      // ---- a draft with a Compliance gate, a task on it and a milestone
      await page.getByRole('button', { name: 'New template', exact: true }).click()
      await page.locator('label:has-text("Name") input').fill(name)
      await page.locator('input[aria-label="Gate 1 name"]').fill('Compliance Sign-off')
      await page.locator('select[aria-label="Gate 1 class"]').selectOption('Compliance')
      await page.getByRole('button', { name: 'Add task', exact: true }).click()
      await page.locator('input[aria-label="Gate 1 task 1 description"]').fill('SOX evidence uploaded')
      await page.getByRole('button', { name: 'Add milestone', exact: true }).click()
      await page.locator('input[aria-label="Milestone 1 name"]').fill('Code complete')
      await expect(page.getByRole('table', { name: 'Milestones' })).toContainText('T−14')
      await scan(page, `the template editor (${scheme})`)
      await page.getByRole('button', { name: 'Create draft', exact: true }).click()

      const checklist = page.getByRole('table', { name: 'Checklist tasks' })
      await expect(checklist).toContainText('SOX evidence uploaded')
      await expect(checklist).toContainText('Compliance Sign-off')
      await expect(page.getByRole('table', { name: 'Milestones' })).toContainText('14 days before the target')
      await expect(page.getByRole('table', { name: 'Gates' })).toContainText('Compliance · locked on releases')
      await expect(page.getByText('A Governance Officer approves this draft')).toBeVisible()
      await expect(templateRow(page, name)).toContainText('1 task')

      // the author cannot approve, and says why
      await expect(page.getByRole('button', { name: 'Approve', exact: true })).toBeDisabled()
      await expect(page.getByText('You created this template, so someone else approves it')).toBeVisible()
      await scan(page, `the draft (${scheme})`)

      // a Release Manager who is not the author cannot approve a Compliance gate either: the Governance Officer does
      const other = await person(browser, scheme, `tg-rm2-${scheme}@example.com`, 'ReleaseManager')
      await openTemplates(other.page)
      await templateRow(other.page, name).click()
      await expect(other.page.getByRole('button', { name: 'Approve', exact: true })).toBeDisabled()
      await expect(other.page.getByText('This template has Compliance gates, so a Governance Officer approves it')).toBeVisible()
      await other.close()

      const gov = await person(browser, scheme, `tg-gov-${scheme}@example.com`, 'GovernanceOfficer')
      await openTemplates(gov.page)
      await templateRow(gov.page, name).click()
      await gov.page.getByRole('button', { name: 'Approve', exact: true }).click()
      await expect(templateRow(gov.page, name)).toContainText('Approved')
      await gov.close()

      // ---- version 2: a new draft under the same name, with the changes from version 1 listed
      await page.reload()
      await openTemplates(page)
      await templateRow(page, name).click()
      await page.getByRole('button', { name: 'New version', exact: true }).click()
      await expect(page.getByRole('heading', { name, exact: true })).toBeVisible()
      await expect(page.getByRole('region', { name: 'Changes from version 1' })).toContainText('No differences from the previous version yet.')
      await expect(templateRow(page, name, 2)).toContainText('Draft')
      await page.getByRole('button', { name: 'Edit', exact: true }).click()
      await expect(page.locator('label:has-text("Name") input')).toHaveAttribute('readonly', '')   // a version keeps its template's name
      await page.getByRole('button', { name: 'Add milestone', exact: true }).click()
      await page.locator('input[aria-label="Milestone 2 name"]').fill('Launch review')
      await page.locator('input[aria-label="Milestone 2 offset in days"]').fill('1')
      await page.getByRole('button', { name: 'Save draft', exact: true }).click()
      const changes = page.getByRole('table', { name: 'Changes from the previous version' })
      await expect(changes).toContainText('Launch review')
      await expect(changes).toContainText('Added')
      await expect(page.getByText('A Governance Officer approves this draft')).toHaveCount(0)   // no Compliance gate changed
      await expect(page.getByRole('table', { name: 'Versions of this template' })).toContainText('Approved')
      await scan(page, `version 2 (${scheme})`)

      // an RTE drafts but cannot approve; the author of version 2 (the RM) cannot either; a different Release Manager can, after a confirmation
      await expect(page.getByRole('button', { name: 'Approve', exact: true })).toBeDisabled()
      const rm3 = await person(browser, scheme, `tg-rm3-${scheme}@example.com`, 'ReleaseManager')
      await openTemplates(rm3.page)
      await templateRow(rm3.page, name, 2).click()
      await rm3.page.getByRole('button', { name: 'Approve', exact: true }).click()
      const dialog = rm3.page.getByRole('dialog')
      await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused()
      await expect(dialog).toContainText('Version 1 is retired in the same step')
      await scan(rm3.page, `the approval dialog (${scheme})`)
      await dialog.getByRole('button', { name: 'Approve and retire the old version' }).click()
      await expect(templateRow(rm3.page, name, 2)).toContainText('Approved')
      await expect(templateRow(rm3.page, name, 1)).toContainText('Retired')
      await expect(rm3.page.getByRole('table', { name: 'Versions of this template' })).toContainText('Retired')
      await rm3.close()
    })

    test('a release made from a template gets its tasks and milestones; milestones move with the target; the Compliance gate is locked', async ({ page, browser }) => {
      const name = `E2E release-from-template ${scheme} ${stamp}`
      const title = `R32.01 From template ${scheme} ${stamp}`
      await signIn(page, `tg-planner-${scheme}@example.com`, 'ReleaseManager')
      const template = await page.evaluate(async n => {
        const r = await fetch('/api/v1/templates', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            name: n, defaultRiskTier: 'Low',
            gates: [
              { gateName: 'Code Freeze', gateClass: 'Standard', offsetDays: 5, requiredBeforeStatus: 'Gated', tasks: [{ taskDescription: 'Tag the repositories' }] },
              { gateName: 'CAB', gateClass: 'Compliance', offsetDays: 1, requiredBeforeStatus: 'Executing', tasks: [{ taskDescription: 'SOX evidence uploaded' }] },
            ],
            milestones: [{ name: 'Code complete', offsetDays: -14 }, { name: 'Hypercare exit', offsetDays: 7 }],
          }),
        })
        if (!r.ok) throw new Error(`${r.status} ${await r.text()}`)
        return await r.json() as { id: string; version: number }
      }, name)
      const gov = await person(browser, scheme, `tg-gov2-${scheme}@example.com`, 'GovernanceOfficer')
      await gov.page.evaluate(async t => {
        const r = await fetch(`/api/v1/templates/${t.id}:approve`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': String(t.version) }, body: '{}' })
        if (!r.ok) throw new Error(`${r.status} ${await r.text()}`)
      }, template)
      await gov.close()

      const trainId = await page.evaluate(async a => {
        const types = await (await fetch('/api/v1/release-types')).json() as { id: string; name: string }[]
        const prod = types.find(t => t.name === 'Production')!
        const r = await fetch('/api/v1/trains', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ title: a.title, targetReleaseDate: '2031-06-13', templateId: a.id, releaseTypeId: prod.id }) })
        if (!r.ok) throw new Error(`${r.status} ${await r.text()}`)
        return (await r.json() as { id: string }).id
      }, { title, id: template.id })

      await page.goto(`/trains/${trainId}`)
      // the milestones came from the template, dated from the target and marked as following it
      const milestones = page.locator('section[aria-label=Milestones]')
      const code = milestones.getByTestId('milestone-row').filter({ hasText: 'Code complete' })
      await expect(code).toContainText('30 May')
      await expect(code).toContainText('moves with the target (T−14)')
      await expect(milestones.getByTestId('milestone-row').filter({ hasText: 'Hypercare exit' })).toContainText('20 Jun')

      // the target moves: so do they
      const moved = await page.evaluate(async id => {
        const t = await (await fetch(`/api/v1/trains/${id}`)).json() as { version: number }
        const r = await fetch(`/api/v1/trains/${id}`, { method: 'PATCH', headers: { 'Content-Type': 'application/json', 'If-Match': String(t.version) }, body: JSON.stringify({ targetReleaseDate: '2031-06-20' }) })
        return r.status
      }, trainId)
      expect(moved).toBe(200)
      await page.reload()
      await expect(page.locator('section[aria-label=Milestones]').getByTestId('milestone-row').filter({ hasText: 'Code complete' })).toContainText('6 Jun')

      // the Compliance gate is locked: no Remove, a class that cannot change, and the reason in words
      const gates = page.locator('section[aria-label="Quality gates"]').getByRole('table', { name: 'Quality gates' })
      await gates.locator('tbody tr', { hasText: 'CAB' }).click()
      const inspector = page.locator('.inspector')
      await expect(inspector.locator('h2')).toHaveText('CAB')
      await expect(inspector).toContainText('Locked by template')
      await expect(inspector).toContainText('0 of 1 done')
      await expect(inspector.getByRole('button', { name: 'Remove gate' })).toHaveCount(0)
      await expect(inspector).toContainText('it cannot be removed or made Standard. To skip it, request a waiver')
      await inspector.getByRole('button', { name: 'Edit gate' }).click()
      await expect(inspector.getByRole('group', { name: 'Edit gate' }).locator('label:has-text("Class") select')).toBeDisabled()
      await expect(inspector).toContainText('came from the approved template, so it stays Compliance')
      await scan(page, `the locked gate (${scheme})`)
      await inspector.getByRole('button', { name: 'Cancel' }).click()

      // the Standard gate of the same template is still the planner's
      await gates.locator('tbody tr', { hasText: 'Code Freeze' }).click()
      await expect(inspector.locator('h2')).toHaveText('Code Freeze')
      await expect(inspector).not.toContainText('Locked by template')
      await expect(inspector.getByRole('button', { name: 'Remove gate' })).toBeVisible()
    })
  })
}
